using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Kraken.Agent.Core.Services;

/// <summary>
///     Utility methods for collecting system metrics and health information.
/// </summary>
public static class SystemMetrics
{
    /// <summary>
    ///     Gets the current RAM usage of the agent process in megabytes.
    /// </summary>
    public static double GetRamUsageMb()
    {
        using var proc = Process.GetCurrentProcess();
        return proc.WorkingSet64 / (1024.0 * 1024.0);
    }

    /// <summary>
    ///     Gets the total RAM of the machine in gigabytes.
    /// </summary>
    public static double GetTotalRamGb()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: Use performance counter or WMI
                var gcMemoryInfo = GC.GetGCMemoryInfo();
                return gcMemoryInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            }

            if (OperatingSystem.IsLinux())
            {
                // Linux: Read /proc/meminfo
                var memInfo = File.ReadAllText("/proc/meminfo");
                var match = Regex.Match(memInfo, @"MemTotal:\s+(\d+)\s+kB");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var totalKb))
                    return totalKb / (1024.0 * 1024.0); // KB to GB
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS: Use sysctl
                var gcMemoryInfo = GC.GetGCMemoryInfo();
                return gcMemoryInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            }

            var fallbackInfo = GC.GetGCMemoryInfo();
            return fallbackInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     Gets the total and free disk space in gigabytes for the current drive.
    /// </summary>
    public static (double TotalGb, double FreeGb) GetDiskUsage()
    {
        var drive = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.IsReady && Environment.CurrentDirectory.StartsWith(d.Name));
        if (drive != null)
        {
            var total = drive.TotalSize / 1_000_000_000.0;
            var free = drive.AvailableFreeSpace / 1_000_000_000.0;
            return (total, free);
        }

        return (0, 0);
    }

    /// <summary>
    ///     Calculates the CPU usage percentage of the agent process.
    /// </summary>
    /// <param name="sampleMs">Duration in milliseconds to sample CPU usage</param>
    public static async Task<double> GetCpuUsageAsync(int sampleMs = 500)
    {
        using var proc = Process.GetCurrentProcess();
        var startCpu = proc.TotalProcessorTime;
        var startTime = DateTime.UtcNow;

        await Task.Delay(sampleMs);

        var endCpu = proc.TotalProcessorTime;
        var endTime = DateTime.UtcNow;

        var cpuUsedMs = (endCpu - startCpu).TotalMilliseconds;
        var elapsedMs = (endTime - startTime).TotalMilliseconds;
        var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * elapsedMs);

        return Math.Round(cpuUsageTotal * 100, 1);
    }

    /// <summary>
    ///     Gets the operating system version string.
    /// </summary>
    public static string GetOsVersion()
    {
        return Environment.OSVersion.ToString();
    }

    /// <summary>
    ///     Gets the agent process uptime in dd:hh:mm:ss format.
    /// </summary>
    public static string GetUptime()
    {
        return (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"dd\:hh\:mm\:ss");
    }

    /// <summary>
    ///     Gets the local IP address of the agent.
    /// </summary>
    public static string GetIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ipAddress = host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));
            return ipAddress?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}