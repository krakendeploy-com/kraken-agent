namespace Kraken.Agent.Core.Tokens;

internal static class SecureTokenStore
{
    public static void SaveRefreshToken(string platform, string rootPath, string refreshToken)
    {
        Kraken.Models.SecureTokenStore.SaveRefreshToken(platform, rootPath, refreshToken);
    }

    public static string? LoadRefreshToken(string platform, string rootPath)
    {
        return Kraken.Models.SecureTokenStore.LoadRefreshToken(platform, rootPath);
    }
}