using OptimusVibe.Config;
using OptimusVibe.Engine;

namespace OptimusVibe.Sync;

/// <summary>Syncs the config repo. Implementations: git CLI (existing checkout) and GitHub API (managed copy).</summary>
public interface ISyncProvider
{
    /// <summary>"git" or "github".</summary>
    string Name { get; }

    /// <summary>Human-readable target, e.g. "jrose462/Optimus-Vibe (main) as jrose462".</summary>
    string Describe();

    bool IsReady(out string reason);

    /// <summary>Fetch remote changes into the local config folder.</summary>
    GitResult Pull();

    /// <summary>Publish local changes under the given paths (absolute, inside the config folder) and pull remote changes.</summary>
    GitResult CommitAndSync(string message, params string[] paths);
}

public sealed record DeviceCode(string UserCode, string VerificationUri, int ExpiresInSeconds, int IntervalSeconds, string Code);

public sealed record GitHubRepoInfo(string FullName, string DefaultBranch, bool Private, string? Description);

public static class SyncProviders
{
    public const string Git = "git";
    public const string GitHub = "github";

    /// <summary>The provider selected by AppSettings.SyncProvider for these paths.</summary>
    public static ISyncProvider For(AppPaths paths)
    {
        var settings = AppPaths.LoadSettings(paths.DataDir);
        if (!IsGitHub(settings)) return new GitCliProvider(paths.RepoRoot);

        var repo = settings.GitHubRepo;
        if (string.IsNullOrWhiteSpace(repo))
            throw new ConfigException("No GitHub config repo chosen yet: open OptimusVibe.exe (or run `optimus-vibe github use <owner/name>`).");
        var token = GitHubAuth.LoadToken();
        return new GitHubMirror(AppPaths.MirrorDir(paths.DataDir, repo), repo,
            settings.GitHubBranch, token is null ? null : new GitHubApi(token), settings.GitHubLogin)
        {
            ActiveConfigRoot = paths.ConfigRoot,
        };
    }

    public static bool IsGitHub(AppSettings settings) =>
        settings.SyncProvider.Equals(GitHub, StringComparison.OrdinalIgnoreCase);
}
