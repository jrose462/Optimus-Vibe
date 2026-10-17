using System.Reflection;

namespace OptimusVibe;

/// <summary>Values baked into the build (see Directory.Build.props / OptimusVibe.Core.csproj).</summary>
public static class BuildInfo
{
    /// <summary>
    /// The GitHub OAuth App client id compiled in from the OptimusVibeGitHubClientId build property ("" when not set).
    /// It is public (it identifies the app, it is not a secret).
    /// </summary>
    public static string GitHubClientId { get; } = Read("OptimusVibeGitHubClientId");

    private static string Read(string key) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";
}
