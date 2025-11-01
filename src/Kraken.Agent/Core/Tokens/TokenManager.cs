using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kraken.Agent.Core.State;
using Kraken.Agent.Models;

namespace Kraken.Agent.Core.Tokens;

internal static class TokenManager
{
    // Now use AgentState.Current for in-memory runtime token values
    public static bool IsTokenExpiringSoon()
    {
        return AgentState.Current.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public static async Task EnsureAccessTokenAsync(AgentSettings settings, string platform)
    {
        if (!IsTokenExpiringSoon()) return;
        await RefreshAsync(settings, platform);
    }

    public static async Task<bool> RefreshAsync(AgentSettings settings, string platform)
    {
        try
        {
            var rootPath = GetRootInstallPath(platform, settings.Agent.Id.ToString());

            // Prefer a refresh token from the secure store (persistent) but fall back to the in-memory state if present
            var refresh = SecureTokenStore.LoadRefreshToken(platform, rootPath);
            if (string.IsNullOrWhiteSpace(refresh))
                // fallback to runtime state (in-memory) if the secure store has none
                refresh = AgentState.Current.RefreshToken;

            if (string.IsNullOrWhiteSpace(refresh))
            {
                Console.WriteLine("⚠️ [Auth Refresh] No refresh token found in secure store or runtime state.");
                return false;
            }

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            // Use the static configured URL (loaded from agentsettings.json)
            var refreshEndpoint = settings.Auth.Url.TrimEnd('/') + "/agent/refresh";

            var resp = await http.PostAsJsonAsync(refreshEndpoint,
                new { RefreshToken = refresh, AgentId = settings.Agent.Id });

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"⚠️ [Auth Refresh] Failed: {resp.StatusCode}");
                return false;
            }

            var tok = await resp.Content.ReadFromJsonAsync<IdentityTokenResponse>();
            if (tok == null)
            {
                Console.WriteLine("⚠️ [Auth Refresh] Invalid response body.");
                return false;
            }

            // Update in-memory access token & expiry (runtime state)
            AgentState.Current.AccessToken = tok.AccessToken;
            AgentState.Current.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn);

            // Rotate refresh token in the secure store and update runtime state
            if (!string.IsNullOrWhiteSpace(tok.RefreshToken))
            {
                SecureTokenStore.SaveRefreshToken(platform, rootPath, tok.RefreshToken);
                AgentState.Current.RefreshToken = tok.RefreshToken;
            }

            Console.WriteLine("✅ Token refreshed successfully (in memory; refresh rotated in secure store).");
            return true;
        }
        catch (HttpRequestException httpEx)
        {
            Console.WriteLine($"⚠️ [Auth Refresh] Network error: {httpEx.Message}");
            return false;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("⚠️ [Auth Refresh] Request timeout.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [Auth Refresh] Unexpected error: {ex.Message}");
            return false;
        }
    }

    private static string GetRootInstallPath(string platform, string agentId)
    {
        return platform == "win-x64" ? Path.Combine("C:\\Kraken\\Agents", agentId) : $"/opt/kraken/agents/{agentId}";
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