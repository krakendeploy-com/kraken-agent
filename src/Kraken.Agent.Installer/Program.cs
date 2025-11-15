using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kraken.Models.Request;
using Kraken.Models.Response;

namespace Kraken.Agent.Installer;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var platform = GetPlatform();
        
        // Check for elevated privileges on Linux
        if (!IsWindows(platform) && !IsRunningAsRoot())
        {
            Console.WriteLine("❌ This installer must be run with root privileges on Linux.");
            Console.WriteLine("   Please run with sudo:");
            Console.WriteLine($"   sudo {System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "./installer"}");
            return 1;
        }
        
        var (orgId, workspaceId, agentIdFromArgs, tags, environments) = ParseArguments(args);
        var logFilePath = Path.Combine(AppContext.BaseDirectory, "kraken-install.log");
        var logWriter = new StreamWriter(logFilePath, true) { AutoFlush = true };
        var dualWriter = new DualWriter(Console.Out, logWriter);
        Console.SetOut(dualWriter);
        Console.SetError(dualWriter);

        var tmpFolder = CreateTemporaryFolder();
        var zipPath = Path.Combine(tmpFolder, "agent.zip");
        var sourceZipUrl = $"https://github.com/krakendeploy-com/kraken-agent/releases/latest/download/Kraken.Agent-{platform}.zip";

        try
        {
            Console.WriteLine($"📦 Downloading from {sourceZipUrl}...");
            await DownloadAgentZip(sourceZipUrl, zipPath);

            Console.WriteLine("📂 Extracting...");
            var agentTempFolder = Path.Combine(tmpFolder, "agent");
            ZipFile.ExtractToDirectory(zipPath, agentTempFolder);

            var version = GetAgentVersion(agentTempFolder);
            if (version == null) return 1;

            string agentId, workspace;
            RegisterAgentApiResponse? agent = null;

            if (!string.IsNullOrWhiteSpace(agentIdFromArgs))
            {
                agentId = agentIdFromArgs!;
                workspace = workspaceId!;
                Console.WriteLine($"🔁 Updating existing agent {agentId}...");
            }
            else if (!string.IsNullOrWhiteSpace(orgId))
            {
                Console.WriteLine("🆕 Registering new agent...");

                // Build description with environment info if provided
                var description = "Registered via Installer";
                if (environments.Any()) description += $" for {environments.Count} environment(s)";

                var input = new RegisterAgentApiRequest
                {
                    Name = Environment.MachineName,
                    Description = description,
                    Tags = tags.ToArray(),
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.OSArchitecture,
                    Environments = environments.Any() ? environments.ToArray() : null
                };

                Console.WriteLine($"  Tags: {(tags.Any() ? string.Join(", ", tags) : "none")}");
                if (environments.Any())
                    Console.WriteLine($"  Environments: {string.Join(", ", environments.Select(e => e.ToString()))}");

                agent = await RegisterAgentAsync(orgId!, workspaceId!, input);
                if (agent == null) return 1;

                agentId = agent.AgentId.ToString();
                workspace = workspaceId!;
            }
            else
            {
                Console.WriteLine("🆕 Something went wrong");
                return 1;
            }

            var rootPath = GetRootInstallPath(platform, agentId);
            var installPath = Path.Combine(rootPath, version);
            var agentExe = GetAgentExecutablePath(installPath, platform);
            var configPath = Path.Combine(installPath, "agentsettings.json");
            var shutdownSignal = Path.Combine(installPath, "shutdown.signal");
            var serviceName = $"kraken-agent-{agentId}";

            if (Directory.Exists(installPath))
            {
                Console.WriteLine($"⚠️ Version {version} is already installed. Skipping installation.");
                return 0;
            }

            if (IsServiceInstalled(serviceName, platform))
            {
                Console.WriteLine("🛑 Stopping running agent...");
                StopAgent(serviceName, agentExe, shutdownSignal, platform);
                await WaitForAgentToExit(agentExe);
            }

            Console.WriteLine("📁 Installing to: " + installPath);
            CopyFiles(agentTempFolder, installPath, true);

            if (!IsWindows(platform))
                RunShell($"chmod +x \"{agentExe}\"");

            Console.WriteLine("📝 Writing config & securing refresh token...");

            // Tokens come from Kraken.Api (which got them from Auth)
            if (agent == null || string.IsNullOrWhiteSpace(agent.AuthAccessToken) ||
                string.IsNullOrWhiteSpace(agent.AuthRefreshToken))
            {
                Console.WriteLine("❌ Registration did not return tokens. Aborting.");
                return 1;
            }

            SecureTokenStore.SaveRefreshToken(platform, rootPath, agent.AuthRefreshToken!);

            await WriteConfigAsync(
                orgId!,
                agentId,
                workspace,
                configPath,
                "https://agent-api.krakendeploy.com",
                agent.AuthUrl ?? "https://auth.krakendeploy.com");

            Console.WriteLine("🚀 Starting agent...");
            var started = TryStartAgent(agentExe, installPath, serviceName, platform);

            if (!started)
            {
                Console.WriteLine("❌ Failed to start agent.");
                return 1;
            }

            Console.WriteLine("✅ Installation complete.");
        }
        finally
        {
            TryDeleteFolder(tmpFolder);
        }

        return 0;
    }

    private static string GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux-x64";
        throw new PlatformNotSupportedException("Unsupported OS platform.");
    }

    private static bool IsWindows(string platform)
    {
        return platform == "win-x64";
    }
    
    private static bool IsRunningAsRoot()
    {
        try
        {
            // On Linux, check if effective user ID is 0 (root)
            var result = RunShell("id -u");
            return result.Trim() == "0";
        }
        catch
        {
            return false;
        }
    }

    private static string GetRootInstallPath(string platform, string agentId)
    {
        var basePath = IsWindows(platform) ? "C:\\Kraken\\Agents" : "/opt/kraken/agents";
        return Path.Combine(basePath, agentId);
    }

    private static string GetAgentExecutablePath(string path, string platform)
    {
        return Path.Combine(path, IsWindows(platform) ? "Kraken.Agent.exe" : "Kraken.Agent");
    }

    private static (string? orgId, string workspaceId, string? agentId, List<string> tags, List<Guid> environments)
        ParseArguments(string[] args)
    {
        string? orgId = null;
        string? workspaceId = null;
        string? agentId = null;
        string? tagsArg = null;
        string? environmentArg = null;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--orgId":
                    orgId = args.ElementAtOrDefault(i + 1);
                    break;
                case "--workspaceId":
                    workspaceId = args.ElementAtOrDefault(i + 1);
                    break;
                case "--agentId":
                    agentId = args.ElementAtOrDefault(i + 1);
                    break;
                case "--tags":
                    tagsArg = args.ElementAtOrDefault(i + 1);
                    break;
                case "--environment":
                    environmentArg = args.ElementAtOrDefault(i + 1);
                    break;
            }

        var hasOrgAndWorkspace = !string.IsNullOrWhiteSpace(orgId) && !string.IsNullOrWhiteSpace(workspaceId);
        var hasAgentAndWorkspace = !string.IsNullOrWhiteSpace(agentId) && !string.IsNullOrWhiteSpace(workspaceId);

        if (!hasOrgAndWorkspace && !hasAgentAndWorkspace)
        {
            Console.WriteLine("❌ You must provide either:");
            Console.WriteLine("   --orgId <id> AND --workspaceId <id>");
            Console.WriteLine("   OR");
            Console.WriteLine("   --agentId <id> AND --workspaceId <id>");
            Environment.Exit(1);
        }

        // Parse tags from comma-separated string
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(tagsArg))
            tags = tagsArg.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

        // Parse environments from comma-separated string to GUIDs
        var environments = new List<Guid>();
        if (!string.IsNullOrWhiteSpace(environmentArg))
        {
            environments = environmentArg.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e) && Guid.TryParse(e, out _))
                .Select(e => Guid.Parse(e))
                .ToList();

            if (environments.Count == 0)
                Console.WriteLine("⚠️ Warning: No valid environment GUIDs found in provided environment parameter");
        }

        return (orgId, workspaceId!, agentId, tags, environments);
    }

    private static string CreateTemporaryFolder()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kraken-{Guid.NewGuid()}");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static async Task DownloadAgentZip(string url, string destination)
    {
        using var client = new HttpClient();
        var zipBytes = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(destination, zipBytes);
    }

    private static string? GetAgentVersion(string extractedPath)
    {
        var versionFile = Path.Combine(extractedPath, "version.txt");
        if (!File.Exists(versionFile)) return null;
        return File.ReadAllText(versionFile).Trim();
    }

    private static void CopyFiles(string source, string target, bool excludeAgentSettings = false)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var destPath = Path.Combine(target, relativePath);

            if (excludeAgentSettings &&
                Path.GetFileName(file).Equals("agentsettings.json", StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, true);
        }
    }

    private static async Task WriteConfigAsync(
        string organizationId, string agentId, string workspaceId,
        string configPath,
        string serverUrl, string authUrl)
    {
        var json = $@"
{{
  ""Agent"": {{
    ""Id"": ""{agentId}"",
    ""WorkspaceId"": ""{workspaceId}"",
    ""OrganizationId"": ""{organizationId}""
  }},
  ""Server"": {{
    ""Url"": ""{serverUrl}""
  }},
  ""Auth"": {{
    ""Url"": ""{authUrl}""
  }}
}}";
        await File.WriteAllTextAsync(configPath, json);
    }

    private static async Task<RegisterAgentApiResponse?> RegisterAgentAsync(string orgId, string workspaceId,
        RegisterAgentApiRequest input)
    {
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(
            $"https://agent-api.krakendeploy.com/organization/{orgId}/workspaces/{workspaceId}/agents", input);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RegisterAgentApiResponse>() : null;
    }

    private static bool IsServiceInstalled(string serviceName, string platform)
    {
        return IsWindows(platform)
            ? RunScWithOutput($"query {serviceName}").Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)
            : RunShell($"systemctl is-enabled {serviceName}").Contains("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void StopAgent(string serviceName, string agentExe, string shutdownSignal, string platform)
    {
        if (Directory.Exists(Path.GetDirectoryName(shutdownSignal)!))
            File.WriteAllText(shutdownSignal, "shutdown");

        if (IsWindows(platform))
            RunSc($"stop {serviceName}");
        else
            RunShell($"systemctl stop {serviceName}");
    }

    private static async Task WaitForAgentToExit(string agentExe)
    {
        var timeout = Task.Delay(10000);

        while (true)
        {
            var stillRunning = Process.GetProcesses()
                .Any(p =>
                {
                    try
                    {
                        return string.Equals(p.MainModule?.FileName, agentExe, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });

            if (!stillRunning) break;

            await Task.Delay(500);
            if (timeout.IsCompleted) break;
        }
    }

    private static bool TryStartAgent(string agentExe, string installPath, string serviceName, string platform)
    {
        try
        {
            if (IsWindows(platform))
            {
                RunSc($"delete {serviceName}");
                RunSc($"create {serviceName} binPath= \"{agentExe}\" start= auto");
                RunSc($"start {serviceName}");
            }
            else
            {
                var serviceContent = $@"[Unit]
Description=Kraken Agent
After=network.target

[Service]
Type=simple
ExecStart={agentExe}
WorkingDirectory={installPath}
Restart=on-failure
User=root

[Install]
WantedBy=multi-user.target";

                var servicePath = $"/etc/systemd/system/{serviceName}.service";
                File.WriteAllText(servicePath, serviceContent);
                RunShell("systemctl daemon-reload");
                RunShell($"systemctl enable {serviceName}");
                RunShell($"systemctl start {serviceName}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to start agent: {ex.Message}");
            return false;
        }
    }

    private static string RunScWithOutput(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        return proc?.StandardOutput.ReadToEnd() ?? "";
    }

    private static void RunSc(string args)
    {
        Process.Start(new ProcessStartInfo("sc.exe", args)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            Verb = "runas"
        })?.WaitForExit();
    }

    private static string RunShell(string command)
    {
        var psi = new ProcessStartInfo("/bin/bash", $"-c \"{command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        return proc?.StandardOutput.ReadToEnd() ?? "";
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}

internal static class SecureTokenStore
{
    public static string GetRefreshBlobPath(string rootPath)
    {
        return Path.Combine(rootPath, "refresh.blob");
    }

    public static void SaveRefreshToken(string platform, string rootPath, string refreshToken)
    {
        Directory.CreateDirectory(rootPath);
        var blobPath = GetRefreshBlobPath(rootPath);

        byte[] cipher;

        if (platform == "win-x64")
        {
            var plain = Encoding.UTF8.GetBytes(refreshToken);
            cipher = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
        }
        else
        {
            var (key, keyPath) = LoadOrCreateLinuxKey();
            cipher = EncryptAesGcm(key, Encoding.UTF8.GetBytes(refreshToken));
            TryChmod600(keyPath);
        }

        File.WriteAllBytes(blobPath, cipher);
        TryChmod600(blobPath);
    }


    // ===== Linux AES-GCM helpers =====
    private static (byte[] key, string keyPath) LoadOrCreateLinuxKey()
    {
        var dir = "/var/lib/kraken/keys";
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "refresh.key");

        if (File.Exists(keyPath))
            return (File.ReadAllBytes(keyPath), keyPath);

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, key);
        return (key, keyPath);
    }

    private static byte[] EncryptAesGcm(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];

        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        var blob = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
        return blob;
    }

    private static void TryChmod600(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("/bin/chmod", $"600 \"{path}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit();
        }
        catch
        {
        }
    }
}
