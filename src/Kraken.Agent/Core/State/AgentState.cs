namespace Kraken.Agent.Core.State;

// Holds runtime-only agent state (tokens, expiry, refresh token).
// This is intentionally separate from AgentSettings (static config loaded from file).
public sealed class AuthState
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
}

public static class AgentState
{
    public static AuthState Current { get; } = new();
}