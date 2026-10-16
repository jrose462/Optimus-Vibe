using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Logging;
using GameProfiles.Sync;

namespace GameProfiles.App.Services;

/// <summary>
/// The app's single entry point for syncing the config repo: the provider chosen in settings (git checkout or
/// GitHub account), falling back to the git CLI if the provider layer isn't available. All methods block — call off the UI thread.
/// </summary>
public static class SyncService
{
    /// <summary>Folders/files the app publishes: manual saves, library opt-outs and the request queue.</summary>
    public static string[] SharedPaths(AppPaths paths) => [paths.SavesDir, paths.LibraryFile, paths.RequestsFile];

    public static GitResult CommitAndSync(AppPaths paths, string message, params string[] files)
    {
        var existing = files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
        try
        {
            return SyncProviders.For(paths).CommitAndSync(message, existing);
        }
        catch (NotImplementedException)
        {
            Log.Warn("Sync providers not available yet — using the git CLI directly");
            var git = new GitSync(paths.RepoRoot);
            var commit = git.Commit(message, existing);
            if (!commit.Ok) return commit;
            var sync = git.Sync();
            return new GitResult(sync.Ok, $"{commit.Message}; {sync.Message}");
        }
    }

    /// <summary>e.g. "jrose462/GameProfiles (main) as jrose462" or "git checkout <repo>".</summary>
    public static string Describe(AppPaths? paths, AppSettings settings)
    {
        if (paths is not null)
        {
            try
            {
                var p = SyncProviders.For(paths);
                var ready = p.IsReady(out var reason);
                return ready ? p.Describe() : $"{p.Describe()} — {reason}";
            }
            catch (NotImplementedException)
            {
                // fall through to a local description
            }
            catch (Exception ex)
            {
                Log.Warn($"Sync provider description failed: {ex.Message}");
                return $"Unavailable: {ex.Message}";
            }
        }
        return IsGitHub(settings)
            ? $"{settings.GitHubRepo} ({settings.GitHubBranch}){(settings.GitHubLogin is { } l ? $" as {l}" : "")}"
            : $"git checkout {paths?.RepoRoot ?? settings.ConfigRoot ?? "(not set)"}";
    }

    public static bool IsGitHub(AppSettings s) => s.SyncProvider.Equals("github", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Download <paramref name="repo"/> into the app's managed copy (%LOCALAPPDATA%\GameProfiles\repos\owner\name)
    /// so the github provider can use it. Returns the config folder inside it.
    /// </summary>
    public static string CloneMirror(string dataDir, string repo, string branch, string token)
    {
        // Same repo + branch already cloned → pulls; a different branch replaces the local copy.
        var result = GitHubMirror.Clone(dataDir, repo, branch, token);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        Log.Info($"Mirror {repo} ({branch}): {result.Message}");
        return Path.Combine(AppPaths.MirrorDir(dataDir, repo), "config");
    }
}
