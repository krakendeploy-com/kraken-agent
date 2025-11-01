namespace Kraken.Agent.Models;

/// <summary>
///     Root configuration for the Kraken Agent.
/// </summary>
public class AgentSettings
{
    public AgentConfig Agent { get; set; } = null!;
    public AgentApiConfig AgentApi { get; set; } = null!;
    public AuthConfig Auth { get; set; } = new();
}

/// <summary>
///     Authentication configuration for accessing the Kraken API (static config only).
/// </summary>
public class AuthConfig
{
    // Base URL for the authentication/API endpoint — comes from agentsettings.json
    public string Url { get; set; } = string.Empty;
}

public class AgentApiConfig
{
    public string Url { get; set; } = string.Empty;
}

public class AgentConfig
{
    public Guid Id { get; init; }
    public string WorkspaceId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
}