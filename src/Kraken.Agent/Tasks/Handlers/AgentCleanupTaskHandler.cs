using System.Runtime.InteropServices;
using Kraken.Agent.Models;
using Kraken.Models.Models;
using Kraken.Models.Tasks;

namespace Kraken.Agent.Tasks.Handlers;

/// <summary>
///     Handles cleanup tasks by removing old deployment artifacts and installations
///     based on retention policies.
/// </summary>
public class AgentCleanupTaskHandler : IAgentCommandTask<AgentCleanupTask>
{
    private readonly AgentSettings _settings;

    public AgentCleanupTaskHandler(AgentSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task HandleAsync(AgentCleanupTask task)
    {
        var enabledPolicies = task.RetentionPolicies?
            .Where(p => p is { Enabled: true })
            .ToList() ?? new List<AgentRetentionPolicyModel>();

        if (enabledPolicies.Count == 0)
        {
            Console.WriteLine("ℹ️ No enabled retention policies provided. Skipping cleanup.");
            return;
        }

        // Aggregate across ALL environments the agent controls (safe superset).
        var effectivePolicy = AggregatePolicies(enabledPolicies);

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var artifactsRoot = isWindows ? @"C:\Kraken\Artifacts" : "/opt/kraken/Artifacts";
        var installsRoot = isWindows ? @"C:\Kraken\Installations" : "/opt/kraken/Installations";

        // Scope to this agent
        var agentArtifactsRoot = Path.Combine(artifactsRoot, _settings.Agent.Id.ToString());
        var agentInstallsRoot = Path.Combine(installsRoot, _settings.Agent.Id.ToString());

        var cutoffUtc = DateTime.UtcNow.AddDays(-effectivePolicy.RetainDays);

        Console.WriteLine($"🧹 Starting cleanup for Agent {_settings.Agent.Id}");
        Console.WriteLine(
            $"   Aggregated policy (all envs): keep last {effectivePolicy.RetainDeployedVersions} versions " +
            $"and anything newer than {effectivePolicy.RetainDays} days (cutoff {cutoffUtc:u}).");

        await CleanupTopLevelAsync(agentArtifactsRoot, effectivePolicy, cutoffUtc, "artifact");
        await CleanupTopLevelAsync(agentInstallsRoot, effectivePolicy, cutoffUtc, "installation");

        Console.WriteLine("✅ Cleanup complete.");
    }

    private static AgentRetentionPolicyModel AggregatePolicies(IEnumerable<AgentRetentionPolicyModel> policies)
    {
        // Keep the MAX of each dimension so we don't prematurely delete for any env.
        var maxKeepVersions = policies.Max(p => Math.Max(0, p.RetainDeployedVersions));
        var maxKeepDays = policies.Max(p => Math.Max(0, p.RetainDays));

        return new AgentRetentionPolicyModel
        {
            Enabled = true,
            Environment = null, // aggregated
            RetainDeployedVersions = maxKeepVersions,
            RetainDays = maxKeepDays
        };
    }

    private static async Task CleanupTopLevelAsync(
        string rootForAgent,
        AgentRetentionPolicyModel policyModel,
        DateTime cutoffUtc,
        string label)
    {
        try
        {
            if (!Directory.Exists(rootForAgent))
            {
                Console.WriteLine($"ℹ️ No {label}s directory found at {rootForAgent}. Nothing to clean.");
                return;
            }

            var families = Directory.EnumerateDirectories(rootForAgent, "*", SearchOption.TopDirectoryOnly).ToList();

            foreach (var familyDir in families) await CleanupFamilyAsync(familyDir, policyModel, cutoffUtc, label);

            foreach (var familyDir in families) TryDeleteIfEmpty(familyDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed while cleaning {label}s at {rootForAgent}: {ex.Message}");
        }
    }

    private static async Task CleanupFamilyAsync(
        string familyDir,
        AgentRetentionPolicyModel policyModel,
        DateTime cutoffUtc,
        string label)
    {
        try
        {
            if (!Directory.Exists(familyDir)) return;

            var versionDirs = Directory.EnumerateDirectories(familyDir, "*", SearchOption.TopDirectoryOnly)
                .Select(p => new DirectoryInfo(p))
                .OrderByDescending(di => di.LastWriteTimeUtc)
                .ToList();

            if (versionDirs.Count == 0) return;

            var keepByCount = versionDirs.Take(Math.Max(0, policyModel.RetainDeployedVersions)).ToHashSet();
            var keepByAge = versionDirs.Where(di => di.LastWriteTimeUtc >= cutoffUtc).ToHashSet();

            var toKeep = new HashSet<DirectoryInfo>(keepByCount);
            foreach (var di in keepByAge) toKeep.Add(di);

            var toDelete = versionDirs.Where(di => !toKeep.Contains(di)).ToList();

            // Optional: safeguard if you have an "in use" marker file (skip those dirs).

            foreach (var dir in toDelete) await DeleteDirectoryAsync(dir.FullName, $"{label} version");

            TryDeleteIfEmpty(familyDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error cleaning {label} family '{familyDir}': {ex.Message}");
        }
    }

    private static async Task DeleteDirectoryAsync(string path, string what)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            Console.WriteLine($"🗑️ Deleting {what}: {path}");
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.IsReadOnly) fi.IsReadOnly = false;
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    {
                        /* best-effort */
                    }

                Directory.Delete(path, true);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to delete {what} '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, false);
                Console.WriteLine($"🗑️ Removed empty folder: {dir}");
            }
        }
        catch
        {
            /* non-fatal */
        }
    }
}