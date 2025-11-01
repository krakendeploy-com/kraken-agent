using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kraken.Models.Models;
using Kraken.Models.Tasks;

namespace Kraken.Agent.Core.Services;

/// <summary>
///     Executes deployment scripts on the target platform (Windows/Linux) with proper logging and error handling.
/// </summary>
public static class ScriptExecutor
{
    /// <summary>
    ///     Executes a deployment script with the specified configuration and streams log output.
    /// </summary>
    /// <param name="scriptBody">The script content to execute</param>
    /// <param name="stepTask">The deployment step configuration containing environment variables</param>
    /// <param name="onOutput">Optional callback for each log line produced during execution</param>
    /// <param name="cancellationToken">Cancellation token to stop execution</param>
    /// <returns>The complete output from the script execution</returns>
    public static async Task<string> RunScriptAsync(
        string scriptBody,
        AgentDeploymentStepTask stepTask,
        Func<ScriptLogLineModel, Task>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = stepTask.AgentId.ToString();
        var releaseVersion = stepTask.ReleaseVersion;
        var environmentName = SanitizeFolderName(stepTask.Environment ?? "default");

        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux";
        var rootPath = GetRootInstallPath(platform, agentId, environmentName, releaseVersion);
        var scriptDir = Path.Combine(rootPath, "script", stepTask.StepOrder.ToString());

        Directory.CreateDirectory(scriptDir);

        var scriptFileName = platform == "windows" ? "deploy.ps1" : "deploy.sh";
        var scriptPath = Path.Combine(scriptDir, scriptFileName);

        if (!File.Exists(scriptPath))
        {
            await File.WriteAllTextAsync(scriptPath, scriptBody, new UTF8Encoding(false));

            if (platform == "linux")
                try
                {
                    Process.Start("chmod", $"+x {scriptPath}")?.WaitForExit();
                }
                catch
                {
                    /* safe to ignore */
                }
        }

        if (platform == "linux")
            try
            {
                Process.Start("chmod", $"+x {scriptPath}")?.WaitForExit();
            }
            catch
            {
                /* safe to ignore */
            }

        var outputBuilder = new StringBuilder();
        var lineCounter = 0;

        var psi = new ProcessStartInfo
        {
            FileName = platform == "windows" ? "powershell.exe" : "/bin/bash",
            Arguments = platform == "windows" ? $"-ExecutionPolicy Bypass -File \"{scriptPath}\"" : scriptPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Add project/environment variables
        foreach (var kv in stepTask.Variables)
            if (!string.IsNullOrEmpty(kv.Key))
                psi.Environment[kv.Key] = kv.Value?.Value ?? "";

        // Add step parameter variables
        foreach (var stepParam in stepTask.StepParameters)
        {
            if (stepParam.ControlType == "SelectArtifact" && stepParam.ArtifactMetadata != null)
            {
                // Add artifact-specific variables
                psi.Environment[$"Kraken.Step.{stepParam.Name}.Name"] = stepParam.ArtifactMetadata.Name;
                psi.Environment[$"Kraken.Step.{stepParam.Name}.Version"] = stepParam.ArtifactMetadata.Version;
                psi.Environment[$"Kraken.Step.{stepParam.Name}.Url"] = stepParam.ArtifactMetadata.Url;
                psi.Environment[$"Kraken.Step.{stepParam.Name}.BasePath"] = stepParam.ArtifactMetadata.BasePath;
            }
            else
            {
                // Add regular step parameter
                psi.Environment[$"Kraken.Step.{stepParam.Name}"] = stepParam.Value;
            }
        }

        // Note: Properties dictionary has been removed from StepTemplateVersion
        // ScriptBody and Syntax are now dedicated properties on the version
        // and are not needed as environment variables

        using var process = new Process { StartInfo = psi };
        process.Start();

        async Task HandleOutput(StreamReader reader, LogLevel defaultLevel)
        {
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) continue;

                var level = InferLogLevel(line, defaultLevel);
                var logLine = new ScriptLogLineModel
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = level,
                    Message = line,
                    Line = Interlocked.Increment(ref lineCounter)
                };

                outputBuilder.AppendLine($"[{logLine.Timestamp:u}] [{logLine.Level}] {logLine.Message}");
                if (onOutput != null) await onOutput(logLine);
            }
        }

        await Task.WhenAll(
            HandleOutput(process.StandardOutput, LogLevel.INFO),
            HandleOutput(process.StandardError, LogLevel.ERROR),
            process.WaitForExitAsync(cancellationToken)
        );

        return outputBuilder.ToString();
    }

    private static LogLevel InferLogLevel(string line, LogLevel defaultLevel)
    {
        var lower = line.ToLowerInvariant();
        if (lower.Contains("error") || lower.Contains("fail")) return LogLevel.ERROR;
        if (lower.Contains("warn")) return LogLevel.WARN;
        if (lower.Contains("info")) return LogLevel.INFO;
        return defaultLevel;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string GetRootInstallPath(string platform, string agentId, string environmentName,
        string releaseVersion)
    {
        var baseRoot = platform == "windows" ? @"C:\Kraken\Installations" : "/opt/kraken/Installations";
        return Path.Combine(baseRoot, agentId, environmentName, releaseVersion);
    }
}