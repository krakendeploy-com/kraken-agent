using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Kraken.Agent.Models;
using Kraken.Models.Tasks;

namespace Kraken.Agent.Tasks.Handlers;

/// <summary>
///     Handles agent self-update tasks by downloading and launching the installer.
/// </summary>
public class AgentUpdateTaskHandler : IAgentCommandTask<AgentUpdateTask>
{
    private readonly AgentSettings _settings;

    public AgentUpdateTaskHandler(AgentSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task HandleAsync(AgentUpdateTask task)
    {
        Console.WriteLine("🔄 Starting agent update process...");

        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
        var updateUrl = $"TBA/Kraken.Agent.Installer-{platform}.zip";

        using var client = new HttpClient();
        var data = await client.GetByteArrayAsync(updateUrl);

        var updateRoot = Path.Combine(Path.GetTempPath(), "kraken_updater");
        var tmpDir = Path.Combine(updateRoot, "tmp");

        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(tmpDir);

        var zipPath = Path.Combine(updateRoot, "Kraken.Agent.Update.zip");
        await File.WriteAllBytesAsync(zipPath, data);

        ZipFile.ExtractToDirectory(zipPath, tmpDir, true);

        var updaterExecutable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Kraken.Agent.Installer.exe"
            : "Kraken.Agent.Installer";

        var updaterPath = Path.Combine(tmpDir, updaterExecutable);

        if (!File.Exists(updaterPath))
        {
            Console.WriteLine($"❌ Updater not found at {updaterPath}");
            return;
        }

        var arguments = $"--agentId {_settings.Agent.Id} --workspaceId {_settings.Agent.WorkspaceId} --debug";
        Console.WriteLine($"🚀 Launching updater with arguments: {arguments}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(updaterPath)!
            }
        };

        process.Start();
        Console.WriteLine("✅ Update process launched successfully");
    }
}