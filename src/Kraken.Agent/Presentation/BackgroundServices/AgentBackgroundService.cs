using Kraken.Agent.Core.Services;
using Kraken.Agent.Models;
using Microsoft.Extensions.Hosting;

namespace Kraken.Agent.Presentation.BackgroundServices;

/// <summary>
///     Windows service host for the Kraken Agent that manages the agent lifecycle.
/// </summary>
public class AgentBackgroundService : BackgroundService
{
    private readonly AgentClient _agent;

    public AgentBackgroundService(AgentSettings settings)
    {
        _agent = new AgentClient(settings);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var shutdownFile = Path.Combine(AppContext.BaseDirectory, "shutdown.signal");

        try
        {
            await _agent.StartPollingAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                if (Directory.Exists(AppContext.BaseDirectory) && File.Exists(shutdownFile))
                {
                    File.Delete(shutdownFile);
                    Console.WriteLine("🛑 Agent shutting down from signal file");
                    break;
                }

                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("🛑 Agent shutting down gracefully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Agent shutting down due to exception: {ex.Message}");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("🛑 Agent StopAsync called - cleaning up...");

        try
        {
            await _agent.StopAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error during agent cleanup: {ex.Message}");
        }

        await base.StopAsync(cancellationToken);
    }
}