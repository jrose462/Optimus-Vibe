using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe.Sync;

/// <summary>
/// The "github" provider: a local copy of the repo's config/ folder (under %LOCALAPPDATA%\OptimusVibe\repos\owner\name)
/// synced through the GitHub git data API — no git install needed. <c>.optimusvibe-sync.json</c> at the mirror root
/// remembers the commit the copy is based on and each file's blob sha at that commit, which is what three-way merges
/// local and remote changes on pull.
/// </summary>
public sealed class GitHubMirror : ISyncProvider
{
    public const string StateFileName = ".optimusvibe-sync.json";
    public const string ConflictSuffix = ".local-conflict";
    private const string ConfigPrefix = "config/";
    private const string SavesPrefix = "config/saves/";
    private const string MarkerFile = "config/devices.yaml";
    private const int MaxPushAttempts = 4; // first try + 3 retries after a non-fast-forward

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GitHubApi? _api;

    /// <param name="mirrorRoot">Usually <see cref="AppPaths.MirrorDir"/>; config/ lives under it.</param>
    /// <param name="api">null when not signed in (IsReady then reports it).</param>
    public GitHubMirror(string mirrorRoot, string repo, string branch, GitHubApi? api, string? login = null)
    {
        MirrorRoot = Path.GetFullPath(mirrorRoot);
        Repo = repo;
        Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        Login = login;
        _api = api;
    }

    public string Name => SyncProviders.GitHub;
    public string Repo { get; }
    public string Branch { get; }
    public string? Login { get; }
    public string MirrorRoot { get; }
    public string ConfigDir => Path.Combine(MirrorRoot, "config");
    public string StateFile => Path.Combine(MirrorRoot, StateFileName);
    private string LegacyStateFile => Path.Combine(MirrorRoot, LegacyMigration.OldStateFileName);

    /// <summary>The config folder the app is actually using; when set and different from <see cref="ConfigDir"/>, IsReady fails.</summary>
    public string? ActiveConfigRoot { get; init; }

    /// <summary>The local copy exists (cloned at least once).</summary>
    public bool IsCloned => (File.Exists(StateFile) || File.Exists(LegacyStateFile)) && File.Exists(Path.Combine(ConfigDir, "devices.yaml"));

    /// <summary>Commit the local copy is based on (null before the first clone).</summary>
    public string? BaseCommit => LoadState()?.BaseCommit;

    /// <summary>When the last successful pull/push finished (UTC).</summary>
    public DateTime? LastSyncUtc => LoadState()?.LastSyncUtc;

    public string Describe() => $"{Repo} ({Branch}){(Login is null ? "" : " as " + Login)}";

    public bool IsReady(out string reason)
    {
        reason = "";
        if (_api is null) reason = "Not signed in to GitHub — sign in first (`optimus-vibe github login`).";
        else if (!IsCloned) reason = $"No local copy of {Repo} yet — set it up with `optimus-vibe github use {Repo}`.";
        else if (LoadState() is { } s && (!s.Repo.Equals(Repo, StringComparison.OrdinalIgnoreCase) || s.Branch != Branch))
            reason = $"The local copy tracks {s.Repo} ({s.Branch}), not {Repo} ({Branch}) — run `optimus-vibe github use {Repo} --branch {Branch}`.";
        else if (ActiveConfigRoot is not null && !SamePath(ActiveConfigRoot, ConfigDir))
            reason = $"The app is using {ActiveConfigRoot}, not the GitHub copy at {ConfigDir} (--config / OPTIMUSVIBE_CONFIG override?).";
        return reason.Length == 0;
    }

    public GitResult Pull()
    {
        if (!IsReady(out var why)) return new(false, why);
        return Locked(async () =>
        {
            var state = LoadState()!;
            var msg = await PullCoreAsync(state).ConfigureAwait(false);
            Log.Info($"github pull {Repo}: {msg}");
            return new GitResult(true, msg);
        });
    }

    public GitResult CommitAndSync(string message, params string[] paths)
    {
        if (!IsReady(out var why)) return new(false, why);
        var scopes = Scopes(paths);
        return Locked(async () =>
        {
            var state = LoadState()!;
            for (var attempt = 1; attempt <= MaxPushAttempts; attempt++)
            {
                var pulled = await PullCoreAsync(state).ConfigureAwait(false);
                var changes = CollectChanges(state, scopes);
                if (changes.Count == 0)
                {
                    Log.Info($"github sync {Repo}: nothing to commit; {pulled}");
                    return new GitResult(true, $"nothing to commit; {pulled}");
                }

                var entries = new List<GitTreeChange>();
                foreach (var (path, content) in changes)
                    entries.Add(new GitTreeChange(path, content is null ? null : await _api!.CreateBlobAsync(Repo, content).ConfigureAwait(false)));
                var tree = await _api!.CreateTreeAsync(Repo, state.BaseTree!, entries).ConfigureAwait(false);
                var commit = await _api.CreateCommitAsync(Repo, message, tree, state.BaseCommit!).ConfigureAwait(false);
                if (await _api.UpdateRefAsync(Repo, Branch, commit).ConfigureAwait(false))
                {
                    state.BaseCommit = commit;
                    state.BaseTree = tree;
                    foreach (var e in entries)
                        if (e.Sha is null) state.Files.Remove(e.Path);
                        else state.Files[e.Path] = e.Sha;
                    state.LastSyncUtc = DateTime.UtcNow;
                    SaveState(state);
                    var msg = $"pushed {entries.Count} change(s){(pulled == UpToDate ? "" : $"; {pulled}")}";
                    Log.Info($"github sync {Repo}: {msg}");
                    return new GitResult(true, msg);
                }
                Log.Info($"github sync {Repo}: {Branch} moved while pushing (attempt {attempt}) — pulling and retrying");
            }
            return new GitResult(false, $"push failed: {Repo} ({Branch}) kept changing while pushing; try again in a moment.");
        });
    }

    /// <summary>
    /// First-time setup: download config/ at the branch head into <see cref="AppPaths.MirrorDir"/>. If a copy of the same
    /// repo/branch already exists it is pulled instead (local changes kept). Fails if the repo has no config/devices.yaml.
    /// </summary>
    public static GitResult Clone(string dataDir, string repo, string branch, string token) =>
        Clone(AppPaths.MirrorDir(dataDir, repo), repo, branch, new GitHubApi(token));

    internal static GitResult Clone(string mirrorRoot, string repo, string branch, GitHubApi api)
    {
        if (!GitHubApi.IsValidRepoName(repo)) return new(false, $"'{repo}' is not a repo name — use owner/name.");
        var mirror = new GitHubMirror(mirrorRoot, repo, branch, api);
        var existing = mirror.LoadState();
        if (mirror.IsCloned && existing is not null && existing.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && existing.Branch == mirror.Branch)
            return mirror.Pull();

        return mirror.Locked(async () =>
        {
            if (Directory.Exists(mirror.ConfigDir)) Directory.Delete(mirror.ConfigDir, recursive: true);
            if (File.Exists(mirror.StateFile)) File.Delete(mirror.StateFile);
            if (File.Exists(mirror.LegacyStateFile)) File.Delete(mirror.LegacyStateFile);
            Directory.CreateDirectory(mirror.MirrorRoot);
            var state = new SyncState { Repo = repo, Branch = mirror.Branch };
            await mirror.PullCoreAsync(state).ConfigureAwait(false);
            Log.Info($"github clone {repo} ({mirror.Branch}): {state.Files.Count} file(s) at {Short(state.BaseCommit)}");
            return new GitResult(true, $"downloaded {state.Files.Count} file(s) from {repo} ({mirror.Branch}) at {Short(state.BaseCommit)}");
        });
    }

    /// <summary>git's blob id: sha1("blob {length}\0" + content), lowercase hex — same as `git hash-object`.</summary>
    public static string GitBlobSha(ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        sha.AppendData(content);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    // ------------------------------------------------------------ pull (three-way merge against the base commit)

    private const string UpToDate = "already up to date";

    private async Task<string> PullCoreAsync(SyncState state)
    {
        var api = _api!;
        var head = await api.GetBranchHeadAsync(Repo, Branch).ConfigureAwait(false);
        if (head == state.BaseCommit && state.BaseTree is not null)
        {
            state.LastSyncUtc = DateTime.UtcNow;
            SaveState(state);
            return UpToDate;
        }

        var treeSha = await api.GetCommitTreeAsync(Repo, head).ConfigureAwait(false);
        var tree = await api.GetTreeAsync(Repo, treeSha, recursive: true).ConfigureAwait(false);
        if (tree.Truncated)
            throw new GitHubException($"{Repo} is too large for GitHub to list in one go; keep the config repo small.");
        var remote = tree.Entries
            .Where(e => e.Type == "blob" && e.Path.StartsWith(ConfigPrefix, StringComparison.Ordinal))
            .ToDictionary(e => e.Path, e => e.Sha, StringComparer.Ordinal);
        if (!remote.ContainsKey(MarkerFile))
            throw new GitHubException($"{Repo} ({Branch}) doesn't look like an Optimus-Vibe config repo (no {MarkerFile}).");

        var local = ScanLocal();
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        async Task<byte[]> Blob(string sha) =>
            blobs.TryGetValue(sha, out var b) ? b : blobs[sha] = await api.GetBlobAsync(Repo, sha).ConfigureAwait(false);

        int updated = 0, deleted = 0, conflicts = 0;
        foreach (var path in remote.Keys.Union(state.Files.Keys).ToList())
        {
            var r = remote.GetValueOrDefault(path);
            var b = state.Files.GetValueOrDefault(path);
            var l = local.GetValueOrDefault(path);
            if (r == b || l == r) continue; // remote unchanged (keep local) / already identical

            var file = LocalPath(path);
            if (l == b)
            {
                // Local untouched since base: take remote.
                if (r is null) { DeleteFile(file); deleted++; }
                else { WriteFile(file, await Blob(r).ConfigureAwait(false)); updated++; }
                continue;
            }

            // Both sides changed.
            conflicts++;
            if (path.StartsWith(SavesPrefix, StringComparison.Ordinal))
            {
                Log.Warn($"github pull: {path} changed here and on GitHub — keeping this device's copy (saves are device-owned)");
                continue;
            }
            if (l is not null)
            {
                File.Copy(file, file + ConflictSuffix, overwrite: true);
                Log.Warn($"github pull: {path} changed here and on GitHub — took GitHub's version, kept yours as {Path.GetFileName(file)}{ConflictSuffix}");
            }
            else
            {
                Log.Warn($"github pull: {path} was deleted here but changed on GitHub — restored GitHub's version");
            }
            if (r is null) DeleteFile(file);
            else WriteFile(file, await Blob(r).ConfigureAwait(false));
        }

        state.BaseCommit = head;
        state.BaseTree = treeSha;
        state.Files = remote;
        state.LastSyncUtc = DateTime.UtcNow;
        SaveState(state);

        var parts = new List<string> { $"pulled {Short(head)}" };
        if (updated > 0) parts.Add($"{updated} updated");
        if (deleted > 0) parts.Add($"{deleted} deleted");
        if (conflicts > 0) parts.Add($"{conflicts} conflict(s)");
        return string.Join(", ", parts);
    }

    // ------------------------------------------------------------ local changes

    /// <summary>Repo-relative scopes for the given absolute paths ("config" when none / outside the mirror).</summary>
    private List<string> Scopes(string[] paths)
    {
        var scopes = new List<string>();
        foreach (var p in paths)
        {
            var rel = Path.GetRelativePath(MirrorRoot, Path.GetFullPath(p)).Replace('\\', '/').TrimEnd('/');
            if (rel == "config" || rel.StartsWith(ConfigPrefix, StringComparison.OrdinalIgnoreCase)) scopes.Add(rel);
            else Log.Warn($"github sync: {p} is outside {ConfigDir}; ignored");
        }
        if (paths.Length == 0) scopes.Add("config");
        return scopes;
    }

    private static bool InScope(string path, List<string> scopes) =>
        scopes.Any(s => path.Equals(s, StringComparison.OrdinalIgnoreCase) || path.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>(path, new content) for added/modified files; (path, null) for deletions — vs the base commit.</summary>
    private List<(string Path, byte[]? Content)> CollectChanges(SyncState state, List<string> scopes)
    {
        var changes = new List<(string, byte[]?)>();
        var local = ScanLocal();
        foreach (var (path, sha) in local)
            if (InScope(path, scopes) && state.Files.GetValueOrDefault(path) != sha)
                changes.Add((path, File.ReadAllBytes(LocalPath(path))));
        foreach (var path in state.Files.Keys)
            if (InScope(path, scopes) && !local.ContainsKey(path))
                changes.Add((path, null));
        return changes;
    }

    /// <summary>Every file under config/ (except .local-conflict copies) → blob sha.</summary>
    private Dictionary<string, string> ScanLocal()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(ConfigDir)) return result;
        foreach (var file in Directory.EnumerateFiles(ConfigDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(ConflictSuffix, StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gp-tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(MirrorRoot, file).Replace('\\', '/');
            result[rel] = GitBlobSha(File.ReadAllBytes(file));
        }
        return result;
    }

    private string LocalPath(string repoPath) => Path.Combine(MirrorRoot, repoPath.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteFile(string file, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".gp-tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, file, overwrite: true);
    }

    private void DeleteFile(string file)
    {
        if (File.Exists(file)) File.Delete(file);
        // Remove now-empty folders up to config/.
        for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length > ConfigDir.Length && SamePathPrefix(dir, ConfigDir); dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
            Directory.Delete(dir);
        }
    }

    // ------------------------------------------------------------ state, locking

    private sealed class SyncState
    {
        public string Repo { get; set; } = "";
        public string Branch { get; set; } = "";
        public string? BaseCommit { get; set; }
        public string? BaseTree { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        /// <summary>Repo path (config/...) → blob sha at BaseCommit.</summary>
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private SyncState? LoadState()
    {
        try
        {
            var stateFile = File.Exists(StateFile) ? StateFile : LegacyStateFile; // older builds wrote .gameprofiles-sync.json
            if (!File.Exists(stateFile)) return null;
            var s = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(stateFile), JsonOpts);
            if (s is not null) s.Files = new Dictionary<string, string>(s.Files, StringComparer.Ordinal);
            return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"github sync state unreadable ({StateFile}): {ex.Message}");
            return null;
        }
    }

    private void SaveState(SyncState state)
    {
        Directory.CreateDirectory(MirrorRoot);
        WriteFile(StateFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOpts)));
        if (File.Exists(LegacyStateFile)) File.Delete(LegacyStateFile);
    }

    /// <summary>Runs the work under a per-mirror named mutex (app + CLI may sync at once); never throws.</summary>
    private GitResult Locked(Func<Task<GitResult>> work)
    {
        var name = @"Local\OptimusVibe.sync." + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(MirrorRoot.ToLowerInvariant())));
        using var mutex = new Mutex(false, name);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return new GitResult(false, "another sync of this repo is still running; try again shortly.");
            return Task.Run(work).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"github sync {Repo} failed: {ex.Message}");
            return new GitResult(false, ex.Message);
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private static string Short(string? sha) => sha is null ? "?" : sha[..Math.Min(7, sha.Length)];

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool SamePathPrefix(string path, string prefix) =>
        path.StartsWith(prefix.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
