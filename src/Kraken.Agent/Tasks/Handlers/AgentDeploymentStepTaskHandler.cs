using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Kraken.Agent.Core.Services;
using Kraken.Agent.Core.Tokens;
using Kraken.Agent.Models;
using Kraken.Models;
using Kraken.Models.Enums;
using Kraken.Models.Models;
using Kraken.Models.Request;
using Kraken.Models.Tasks;
using AgentState = Kraken.Agent.Core.State.AgentState;

namespace Kraken.Agent.Tasks.Handlers;

/// <summary>
///     Handles deployment step tasks including artifact downloading and script execution.
/// </summary>
public class AgentDeploymentStepTaskHandler : IAgentCommandTask<AgentDeploymentStepTask>
{
    private readonly AgentSettings _settings;

    public AgentDeploymentStepTaskHandler(AgentSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task HandleAsync(AgentDeploymentStepTask stepTask)
    {
        var logs = new List<ScriptLogLineModel>();
        var buffer = new List<ScriptLogLineModel>();
        var lastFlush = DateTime.UtcNow;
        bool success;
        var lineCounter = 0;

        ScriptLogLineModel CreateLine(string message, LogLevel level)
        {
            return new ScriptLogLineModel
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Message = message,
                Line = lineCounter++
            };
        }

        async Task AddLogAsync(string message, LogLevel level)
        {
            var line = CreateLine(message, level);
            logs.Add(line);
            buffer.Add(line);
            Console.WriteLine($"[{line.Timestamp:u}] [{line.Level}] {line.Message}");

            if (buffer.Count >= 10 || DateTime.UtcNow - lastFlush > TimeSpan.FromSeconds(2))
                await FlushAsync();
        }

        async Task FlushAsync()
        {
            if (buffer.Count == 0) return;

            var batch = new DeployLogBatchModel
            {
                DeploymentId = stepTask.DeploymentId,
                StepId = stepTask.StepOrder,
                AgentId = _settings.Agent.Id,
                Logs = buffer.ToList()
            };

            try
            {
                var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
                using var client = await CreateAuthedClientAsync(platform);
                var url =
                    $"{_settings.AgentApi.Url}/organization/{_settings.Agent.OrganizationId}/workspaces/{_settings.Agent.WorkspaceId}/agents/{_settings.Agent.Id}/post-logs";
                var response = await client.PostAsJsonAsync(url, batch);

                if (response.IsSuccessStatusCode)
                {
                    buffer.Clear();
                    lastFlush = DateTime.UtcNow;
                }
                else
                {
                    Console.WriteLine($"⚠️ Failed to post logs: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Exception posting logs: {ex.Message}");
            }
        }

        try
        {
            await ReportStepStarted(stepTask);
            var platformHuman = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux";
            var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";

            // Build a combined variables dictionary for script resolution
            var allVariables = new Dictionary<string, VariableValueModel>(stepTask.Variables);

            // STEP 1: Download artifacts and add step parameter variables
            foreach (var stepParam in stepTask.StepParameters)
                if (stepParam.ControlType == "SelectArtifact" && stepParam.ArtifactMetadata != null)
                {
                    var artifact = stepParam.ArtifactMetadata;
                    var artifactName = artifact.Name;
                    var artifactVersion = artifact.Version;
                    var packageUrl = artifact.Url;

                    var targetDirectory = Path.Combine(
                        platformHuman == "windows"
                            ? @"C:\Kraken\Artifacts"
                            : "/opt/kraken/Artifacts", // Platform-specific root path
                        _settings.Agent.Id.ToString(),
                        artifactName,
                        artifactVersion
                    );

                    // Ensure the directory exists
                    Directory.CreateDirectory(targetDirectory);

                    using var httpClient = new HttpClient();
                    using var response =
                        await httpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                                   ?? Path.GetFileName(new Uri(packageUrl).AbsolutePath);
                    var filePath = Path.Combine(targetDirectory, fileName);

                    if (File.Exists(filePath))
                    {
                        await AddLogAsync($"Artifact '{artifactName}' already exists at {filePath}, skipping download.",
                            LogLevel.INFO);
                    }
                    else
                    {
                        await AddLogAsync($"Downloading artifact '{artifactName}' v{artifactVersion}...",
                            LogLevel.INFO);
                        await using var fs = File.Create(filePath);
                        await response.Content.CopyToAsync(fs);
                        await AddLogAsync($"Downloaded artifact '{artifactName}' to {filePath}", LogLevel.INFO);
                    }

                    // Add artifact-specific variables using the new naming convention: Kraken.Step.{ParamName}.{Property}
                    allVariables[$"Kraken.Step.{stepParam.Name}.Name"] =
                        new VariableValueModel(artifactName, VariableValueType.Text);
                    allVariables[$"Kraken.Step.{stepParam.Name}.Version"] =
                        new VariableValueModel(artifactVersion, VariableValueType.Text);
                    allVariables[$"Kraken.Step.{stepParam.Name}.Url"] =
                        new VariableValueModel(packageUrl, VariableValueType.Text);
                    allVariables[$"Kraken.Step.{stepParam.Name}.BasePath"] =
                        new VariableValueModel(targetDirectory, VariableValueType.Text);
                }
                else
                {
                    // Add regular step parameter variables
                    allVariables[$"Kraken.Step.{stepParam.Name}"] =
                        new VariableValueModel(stepParam.Value, VariableValueType.Text);
                }

            // STEP 2: Prepare and run script
            await AddLogAsync("Preparing script for execution...", LogLevel.INFO);

            var preparedScript = PrepareScript(stepTask.ScriptToExecute, allVariables, platformHuman);

            var cancellation = new CancellationTokenSource();

            await ScriptExecutor.RunScriptAsync(
                preparedScript,
                stepTask,
                async line =>
                {
                    line.Line = lineCounter++;
                    logs.Add(line);
                    buffer.Add(line);
                    Console.WriteLine($"[{line.Timestamp:u}] [{line.Level}] {line.Message}");

                    if (buffer.Count >= 10 || DateTime.UtcNow - lastFlush > TimeSpan.FromSeconds(2))
                        await FlushAsync();
                },
                cancellation.Token);

            await AddLogAsync("Script execution completed successfully.", LogLevel.INFO);
            success = true;
        }
        catch (Exception ex)
        {
            await AddLogAsync($"[ERROR]: {ex.Message}", LogLevel.ERROR);
            await AddLogAsync(ex.StackTrace ?? "", LogLevel.ERROR);
            success = false;
        }

        await FlushAsync(); // Flush any remaining logs

        var stepResult = new AgentStepFinishedApiRequest
        {
            DeploymentId = stepTask.DeploymentId,
            AgentId = _settings.Agent.Id,
            Status = success ? AgentStepStatus.Successful : AgentStepStatus.Failed,
            Logs = string.Join("\n",
                logs.OrderBy(l => l.Line).Select(l => $"[{l.Timestamp:u}] [{l.Level}] {l.Message}")),
            StepId = stepTask.StepOrder
        };

        await ReportStepDone(stepResult);
    }

    private async Task ReportStepDone(AgentStepFinishedApiRequest stepApiRequest)
    {
        try
        {
            var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
            using var client = await CreateAuthedClientAsync(platform);
            var url =
                $"{_settings.AgentApi.Url}/organization/{_settings.Agent.OrganizationId}/workspaces/{_settings.Agent.WorkspaceId}/agents/{_settings.Agent.Id}/step-result";
            var response = await client.PostAsJsonAsync(url, stepApiRequest);
            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"⚠️ Failed to report step result: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Exception reporting step result: {ex.Message}");
        }
    }

    private async Task ReportStepStarted(AgentDeploymentStepTask stepTask)
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
        using var client = await CreateAuthedClientAsync(platform);
        var url =
            $"{_settings.AgentApi.Url}/organization/{_settings.Agent.OrganizationId}/workspaces/{_settings.Agent.WorkspaceId}/agents/{_settings.Agent.Id}/deployment{stepTask.DeploymentId}/step/{stepTask.StepOrder}/started";
        try
        {
            var resp = await client.PutAsync(url, null);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"⚠️ Failed to notify step started: {resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Exception while notifying step started: {ex.Message}");
        }
    }

    /// <summary>
    ///     Replaces $Kraken.(Step|Project|Environment).Key tokens in script using the Variables dictionary,
    ///     resolving nested references with precedence Step > Project > Environment, then wraps for platform.
    /// </summary>
    private static string PrepareScript(string rawScript, Dictionary<string, VariableValueModel> variables,
        string platform = "linux")
    {
        var resolver = new VariableResolver(variables);
        var replaced = resolver.ReplaceVariablesInScript(rawScript);

        // Wrap script for target platform
        if (platform.Equals("linux", StringComparison.OrdinalIgnoreCase))
            return @$"#!/bin/bash
                        set -euo pipefail
                        (
                            {replaced}
                        )";

        if (platform.Equals("windows", StringComparison.OrdinalIgnoreCase))
            return @$"
$ErrorActionPreference = ""Stop""

try {{
{replaced}
}} catch {{
    Write-Host 'ERROR: ' + $_.Exception.Message
    exit 1
}}

exit 0
";

        return replaced;
    }

    private async Task<HttpClient> CreateAuthedClientAsync(string platform)
    {
        await TokenManager.EnsureAccessTokenAsync(_settings, platform);

        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentState.Current.AccessToken);
        return client;
    }
}