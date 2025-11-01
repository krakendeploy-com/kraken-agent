using System.Runtime.InteropServices;
using Kraken.Agent.Core.Services;
using Kraken.Agent.Models;
using Kraken.Agent.Presentation.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

// Parse environment from command line arguments (e.g., "Staging", "Production")
var environment = args.Length > 0 ? args[0] : null;

// Build configuration from JSON files
var baseConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("agentsettings.json", false, true);

if (!string.IsNullOrWhiteSpace(environment)) baseConfig.AddJsonFile($"agentsettings.{environment}.json", true, true);

var config = baseConfig.Build();
var agentSettings = config.Get<AgentSettings>();

if (agentSettings == null)
{
    Console.WriteLine("❌ Failed to load agent settings");
    return;
}

var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

// Run as Windows service or Linux daemon based on platform
if (isWindows)
    await RunWindowsService(args, agentSettings);
else
    await RunLinuxService(agentSettings);

Console.WriteLine("🛑 Agent stopped.");

async Task RunWindowsService(string[] strings, AgentSettings? agentSettings1)
{
    var builder = Host.CreateApplicationBuilder(strings);

    builder.Services.Configure<HostOptions>(options =>
    {
        if (WindowsServiceHelpers.IsWindowsService())
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    });

    if (WindowsServiceHelpers.IsWindowsService())
        builder.Services.AddWindowsService();

    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("agentsettings.json", false, true);

    if (agentSettings1?.Agent.Id == null || string.IsNullOrWhiteSpace(agentSettings1.AgentApi.Url))
    {
        File.WriteAllText("agent-crash.log", "Missing Agent ID or URL in configuration.");
        throw new InvalidOperationException("Agent configuration is invalid.");
    }

    builder.Services.AddSingleton(agentSettings1);
    builder.Services.AddHostedService<AgentBackgroundService>();

    builder.Logging.ClearProviders();

    var host = builder.Build();
    await host.RunAsync();
}

async Task RunLinuxService(AgentSettings? agentSettings2)
{
    if (agentSettings2 == null)
    {
        Console.WriteLine("❌ Agent settings cannot be null");
        return;
    }

    var agent = new AgentClient(agentSettings2);
    var shutdownFile = Path.Combine(AppContext.BaseDirectory, "shutdown.signal");
    var shutdownCts = new CancellationTokenSource();

    // Start agent polling loop
    await agent.StartPollingAsync();
    Console.WriteLine("✅ Agent running...");

    // Monitor for shutdown signal file
    _ = Task.Run(async () =>
    {
        while (!shutdownCts.Token.IsCancellationRequested)
        {
            if (File.Exists(shutdownFile))
            {
                Console.WriteLine("🛑 Shutdown signal detected. Exiting...");
                try
                {
                    File.Delete(shutdownFile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to delete shutdown signal: {ex.Message}");
                }

                shutdownCts.Cancel();
                break;
            }

            await Task.Delay(1000, shutdownCts.Token);
        }
    }, shutdownCts.Token);

    // Wait until either shutdown signal or app close
    try
    {
        await Task.Delay(Timeout.Infinite, shutdownCts.Token);
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine("🛑 Agent shutting down");
    }

    // Cleanup agent
    await agent.StopAsync();
}