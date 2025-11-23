using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Kraken.Agent.Core.Tokens;
using Kraken.Agent.Models;
using Kraken.Agent.Tasks.Handlers;
using Kraken.Models.Enums;
using Kraken.Models.Request;
using Kraken.Models.Response;
using Kraken.Models.Tasks;
using Newtonsoft.Json;

namespace Kraken.Agent.Core.Services;

/// <summary>
///     Core agent client responsible for polling the Kraken API for tasks,
///     managing authentication, and coordinating task execution.
/// </summary>
public class AgentClient
{
    private const int DefaultPollingIntervalSeconds = 30;

    private readonly AgentCleanupTaskHandler _cleanupTaskHandler;
    private readonly AgentDeploymentStepTaskHandler _deploymentStepTaskHandler;
    private readonly Random _random = new();
    private readonly AgentSettings _settings;
    private readonly AgentUpdateTaskHandler _updateTaskHandler;
    private readonly string _version;

    private AgentState _state;
    private AgentStatus _status;

    public AgentClient(AgentSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        _deploymentStepTaskHandler = new AgentDeploymentStepTaskHandler(_settings);
        _updateTaskHandler = new AgentUpdateTaskHandler(_settings);
        _cleanupTaskHandler = new AgentCleanupTaskHandler(_settings);
        PollingInterval = TimeSpan.FromSeconds(DefaultPollingIntervalSeconds);

        // Initialize status and state
        _state = AgentState.Waiting;
    }

    public TimeSpan PollingInterval { get; set; }

    /// <summary>
    ///     Starts the agent polling loop to continuously check for and execute tasks.
    /// </summary>
    public async Task StartPollingAsync()
    {
        Console.WriteLine("🚀 Starting agent polling...");

        // Set initial healthy status when starting
        _status = AgentStatus.Healthy;
        _state = AgentState.Waiting;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var platform = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
                    await TokenManager.EnsureAccessTokenAsync(_settings, platform);

                    var task = await GetNextTaskAsync();
                    if (task != null && task.Data != null)
                    {
                        // Set state to Busy before handling task
                        _state = AgentState.Busy;
                        _status = AgentStatus.Healthy;

                        await HandleTaskAsync(task.Data);

                        // Reset state to Waiting after task completion
                        _state = AgentState.Waiting;
                        _status = AgentStatus.Healthy;
                    }
                    else
                    {
                        // No task received, agent is healthy and waiting
                        if (_status == AgentStatus.Offline)
                            // Recovered from offline state
                            _status = AgentStatus.Healthy;
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    Console.WriteLine($"⚠️ API connection failed: {httpEx.Message}");
                    Console.WriteLine("   Will retry on next poll...");
                    _status = AgentStatus.Unhealthy;
                    _state = AgentState.Waiting;
                }
                catch (TaskCanceledException tcEx)
                {
                    Console.WriteLine($"⚠️ API request timed out: {tcEx.Message}");
                    Console.WriteLine("   Will retry on next poll...");
                    _status = AgentStatus.Unhealthy;
                    _state = AgentState.Waiting;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Polling failed: {ex.Message}");
                    Console.WriteLine($"   Exception type: {ex.GetType().Name}");
                    Console.WriteLine("   Will retry on next poll...");
                    _status = AgentStatus.Unhealthy;
                    _state = AgentState.Waiting;
                }

                // Add jitter to prevent thundering herd
                var jitterSeconds = _random.Next(-1, 2);
                var delay = PollingInterval + TimeSpan.FromSeconds(jitterSeconds);
                delay = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;

                await Task.Delay(delay);
            }
        });
    }

    private HttpClient CreateAuthedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", State.AgentState.Current.AccessToken);
        return client;
    }

    private async Task<ApiResult<AgentTaskResponse?>> GetNextTaskAsync()
    {
        try
        {
            using var client = CreateAuthedClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var cpu = await SystemMetrics.GetCpuUsageAsync();
            var ram = SystemMetrics.GetRamUsageMb();
            var ramTotal = SystemMetrics.GetTotalRamGb();
            var (diskTotal, diskFree) = SystemMetrics.GetDiskUsage();

            var input = new AgentNextTaskApiRequest
            {
                Version = _version,
                HealthStatus = _status,
                TaskState = _state,
                Timestamp = DateTime.UtcNow,
                CpuUsagePercent = cpu,
                RamUsageMb = ram,
                RamTotalGb = ramTotal,
                DiskTotalGb = diskTotal,
                DiskFreeGb = diskFree,
                OsVersion = SystemMetrics.GetOsVersion(),
                AgentUptime = SystemMetrics.GetUptime(),
                IpAddress = SystemMetrics.GetIpAddress()
            };

            var url =
                $"{_settings.AgentApi.Url}/organization/{_settings.Agent.OrganizationId}/workspaces/{_settings.Agent.WorkspaceId}/agents/{_settings.Agent.Id}/next-task";
            var response = await client.PostAsJsonAsync(url, input);

            // Handle 401 Unauthorized by refreshing token and retrying once
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var platform = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
                if (await TokenManager.RefreshAsync(_settings, platform))
                {
                    using var client2 = CreateAuthedClient();
                    client2.Timeout = TimeSpan.FromSeconds(30);
                    response = await client2.PostAsJsonAsync(url, input);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                    // Conflict status, keep current status
                    return null;

                Console.WriteLine($"⚠️ Task request failed. Status: {response.StatusCode}");
                _status = AgentStatus.Offline;
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                // No tasks available, keep current status
                PollingInterval = TimeSpan.FromSeconds(DefaultPollingIntervalSeconds);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<ApiResult<AgentTaskResponse>>();

            // Don't override status here - let the caller handle it
            return result;
        }
        catch (HttpRequestException httpEx)
        {
            Console.WriteLine($"⚠️ Network error while fetching task: {httpEx.Message}");
            _status = AgentStatus.Offline;
            return null;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("⚠️ Request timeout while fetching task");
            _status = AgentStatus.Offline;
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Unexpected error in GetNextTaskAsync: {ex.Message}, Stacktrace: {ex.StackTrace}");
            _status = AgentStatus.Offline;
            return null;
        }
    }

    private async Task HandleTaskAsync(AgentTaskResponse task)
    {
        try
        {
            switch (task.TaskType)
            {
                case AgentTaskType.Deploy:
                    PollingInterval = TimeSpan.FromSeconds(5);
                    await _deploymentStepTaskHandler.HandleAsync(
                        JsonConvert.DeserializeObject<AgentDeploymentStepTask>(task.Payload.ToString()));
                    break;
                case AgentTaskType.Update:
                    // Set Updating status for update tasks
                    _status = AgentStatus.Updating;
                    await _updateTaskHandler.HandleAsync(
                        JsonConvert.DeserializeObject<AgentUpdateTask>(task.Payload.ToString()));
                    break;
                case AgentTaskType.Cleanup:
                    await _cleanupTaskHandler.HandleAsync(
                        JsonConvert.DeserializeObject<AgentCleanupTask>(task.Payload.ToString()));
                    break;
                default:
                    Console.WriteLine($"⚠️ Unknown task type: {task.TaskType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Task execution failed: {ex.Message}");
            Console.WriteLine($"   Task Type: {task.TaskType}");
            Console.WriteLine($"   Exception: {ex.GetType().Name}");
            _status = AgentStatus.Unhealthy;
            throw; // Re-throw so the outer catch can handle it
        }
    }

    public async Task HandleOfflineAsync()
    {
        using var httpClient = CreateAuthedClient();
        var url =
            $"{_settings.AgentApi.Url}/organization/{_settings.Agent.OrganizationId}/workspaces/{_settings.Agent.WorkspaceId}/agents/{_settings.Agent.Id}/set-offline";

        try
        {
            var response = await httpClient.PutAsync(url, null);

            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"❌ Failed to report offline status. HTTP {response.StatusCode}");
            else
                Console.WriteLine("✅ Agent set to offline successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Exception while reporting offline status: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        await HandleOfflineAsync();
    }

    private record IdentityTokenResponse(
        [property: JsonPropertyName("tokenType")]
        string TokenType,
        [property: JsonPropertyName("accessToken")]
        string AccessToken,
        [property: JsonPropertyName("expiresIn")]
        int ExpiresIn,
        [property: JsonPropertyName("refreshToken")]
        string RefreshToken);
}