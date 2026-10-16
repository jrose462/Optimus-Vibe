using System.Net;
using System.Text;
using OptimusVibe.Logging;

namespace OptimusVibe.Sync;

/// <summary>Result of <see cref="ConfigRepoProbe.CheckAsync"/>.</summary>
/// <param name="Accessible">The repo (and branch, unless it's empty) exists and the account can read it.</param>
/// <param name="IsConfigRepo">config/devices.yaml exists on the branch — ready for GitHubMirror.Clone.</param>
/// <param name="IsEmpty">The repo has no commits at all.</param>
/// <param name="Message">User-facing summary.</param>
public sealed record ConfigRepoStatus(bool Accessible, bool IsConfigRepo, bool IsEmpty, string Message)
{
    /// <summary>Offer "Seed this repo": reachable but not (yet) a config repo.</summary>
    public bool CanSeed => Accessible && !IsConfigRepo;
}

/// <summary>Checks whether a chosen repo is an Optimus-Vibe config repo (so the UI can offer to seed it).</summary>
public static class ConfigRepoProbe
{
    public const string MarkerFile = "config/devices.yaml";

    public static async Task<ConfigRepoStatus> CheckAsync(GitHubApi api, string repo, string branch, CancellationToken ct = default)
    {
        if (!GitHubApi.IsValidRepoName(repo)) return new(false, false, false, $"'{repo}' is not a repo name — use owner/name.");
        string head;
        try
        {
            head = await api.GetBranchHeadAsync(repo, branch, ct).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            return new(true, false, true, $"{repo} is empty. Seed it with this device's config to start using it.");
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return new(false, false, false, ex.Message);
        }

        var treeSha = await api.GetCommitTreeAsync(repo, head, ct).ConfigureAwait(false);
        var tree = await api.GetTreeAsync(repo, treeSha, recursive: true, ct).ConfigureAwait(false);
        var hasMarker = tree.Entries.Any(e => e.Type == "blob" && e.Path == MarkerFile);
        if (hasMarker) return new(true, true, false, $"{repo} ({branch}) is an Optimus-Vibe config repo.");
        return new(true, false, false,
            $"{repo} ({branch}) isn't an Optimus-Vibe config repo yet (no {MarkerFile}). Seed it with this device's config to use it.");
    }
}

/// <summary>What <see cref="ConfigRepoSeeder.SeedAsync"/> committed.</summary>
public sealed record SeedResult(string CommitSha, IReadOnlyList<string> Paths);

/// <summary>
/// Turns an empty / freshly created repo into a config repo: one commit (git data API: blobs → tree → commit → ref)
/// with config/devices.yaml, config/games/*.yaml, config/schema/**, a fresh config/library.yaml and
/// config/requests.yaml, and a short README.md. saves/ and *.local-conflict copies are never seeded.
/// </summary>
public static class ConfigRepoSeeder
{
    public const string CommitMessage = "Seed Optimus-Vibe config";

    internal const string LibraryYaml =
        "# Optimus-Vibe library choices, shared by every machine (synced). Detected games listed in optOut are not managed.\n" +
        "# Keys are \"store:id\" (steam:<appid>, battlenet:<code>, ea:<offer>, epic:<AppName>, gog:<id>, xbox:<PFN>).\n" +
        "optOut: []\n";

    internal const string RequestsYaml =
        "# Optimus-Vibe request queue: installed games that have no config/games/<id>.yaml yet (written by the app).\n" +
        "requests: []\n";

    internal static string Readme(string repo) =>
        $"# {repo.Split('/').Last()}\n\n" +
        "Config repo for [Optimus-Vibe](https://github.com/jrose462/Optimus-Vibe): per-device graphics presets, manual saves and " +
        "library choices, synced between your PCs and handhelds.\n\n" +
        "- `config/devices.yaml` — device profiles (desktop, handheld...)\n" +
        "- `config/games/` — one file per game with recommended presets\n" +
        "- `config/saves/` — manual saves made in the app\n" +
        "- `config/library.yaml`, `config/requests.yaml` — library opt-outs and games waiting for presets\n\n" +
        "Managed by the app; edit by hand only if you know the format (see `config/schema/`).\n";

    /// <summary>Repo path (config/...) → content, from a local config folder.</summary>
    public static IReadOnlyDictionary<string, byte[]> CollectSeedFiles(string seedConfigDir)
    {
        var root = Path.GetFullPath(seedConfigDir);
        var devices = Path.Combine(root, "devices.yaml");
        if (!File.Exists(devices)) throw new GitHubException($"Can't seed from {root}: it has no devices.yaml.");

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/devices.yaml"] = File.ReadAllBytes(devices),
            ["config/library.yaml"] = Encoding.UTF8.GetBytes(LibraryYaml),
            ["config/requests.yaml"] = Encoding.UTF8.GetBytes(RequestsYaml),
        };
        var games = Path.Combine(root, "games");
        if (Directory.Exists(games))
            foreach (var f in Directory.EnumerateFiles(games, "*.yaml", SearchOption.TopDirectoryOnly))
                files["config/games/" + Path.GetFileName(f)] = File.ReadAllBytes(f);
        var schema = Path.Combine(root, "schema");
        if (Directory.Exists(schema))
            foreach (var f in Directory.EnumerateFiles(schema, "*", SearchOption.AllDirectories))
            {
                if (Skip(f)) continue;
                files["config/schema/" + Path.GetRelativePath(schema, f).Replace('\\', '/')] = File.ReadAllBytes(f);
            }
        return files;
    }

    private static bool Skip(string file) =>
        file.EndsWith(GitHubMirror.ConflictSuffix, StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".gp-tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Commits the seed to <paramref name="branch"/> of <paramref name="repo"/>. Refuses a repo that already has
    /// config/devices.yaml. An empty repo (no commits) first gets README.md through the contents API, since the git data
    /// API can't write to an empty repo; everything else lands in one commit.
    /// </summary>
    public static Task<SeedResult> SeedAsync(GitHubApi api, string repo, string branch, string seedConfigDir, CancellationToken ct = default) =>
        SeedAsync(api, repo, branch, seedConfigDir, Task.Delay, ct);

    internal static async Task<SeedResult> SeedAsync(GitHubApi api, string repo, string branch, string seedConfigDir,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        if (!GitHubApi.IsValidRepoName(repo)) throw new GitHubApiException(null, $"'{repo}' is not a repo name — use owner/name.");
        branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        var seed = CollectSeedFiles(seedConfigDir);
        var readme = Encoding.UTF8.GetBytes(Readme(repo));

        var head = await HeadOrInitAsync(api, repo, branch, readme, delay, ct).ConfigureAwait(false);
        var baseTree = await api.GetCommitTreeAsync(repo, head, ct).ConfigureAwait(false);
        var tree = await api.GetTreeAsync(repo, baseTree, recursive: true, ct).ConfigureAwait(false);
        if (tree.Entries.Any(e => e.Type == "blob" && e.Path == ConfigRepoProbe.MarkerFile))
            throw new GitHubException($"{repo} ({branch}) is already an Optimus-Vibe config repo — use it instead of seeding.");

        var files = new SortedDictionary<string, byte[]>(seed.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        // README: write ours unless the repo has a real one (auto_init's is just "# name" + description).
        var existingReadme = tree.Entries.FirstOrDefault(e => e.Type == "blob" && e.Path.Equals("README.md", StringComparison.OrdinalIgnoreCase));
        if (existingReadme is null || existingReadme.Size is null or < 300)
            files[existingReadme?.Path ?? "README.md"] = readme;

        var changes = new List<GitTreeChange>();
        foreach (var (path, content) in files)
        {
            var sha = GitHubMirror.GitBlobSha(content);
            if (tree.Entries.Any(e => e.Path == path && e.Sha == sha)) continue; // identical (e.g. README from init)
            changes.Add(new GitTreeChange(path, await api.CreateBlobAsync(repo, content, ct).ConfigureAwait(false)));
        }
        var newTree = await api.CreateTreeAsync(repo, baseTree, changes, ct).ConfigureAwait(false);
        var commit = await api.CreateCommitAsync(repo, CommitMessage, newTree, head, ct).ConfigureAwait(false);
        if (!await api.UpdateRefAsync(repo, branch, commit, ct).ConfigureAwait(false))
            throw new GitHubException($"{repo} ({branch}) changed while seeding; try again.");
        Log.Info($"seeded {repo} ({branch}) with {changes.Count} file(s) at {commit[..Math.Min(7, commit.Length)]}");
        return new SeedResult(commit, changes.Select(c => c.Path).ToList());
    }

    private static async Task<string> HeadOrInitAsync(GitHubApi api, string repo, string branch, byte[] readme,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var initialized = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await api.GetBranchHeadAsync(repo, branch, ct).ConfigureAwait(false);
            }
            catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.Conflict && !initialized)
            {
                // Empty repo: create the first commit (and the branch) through the contents API.
                await api.PutFileAsync(repo, "README.md", readme, "Initial commit", branch, ct: ct).ConfigureAwait(false);
                initialized = true;
                continue;
            }
            catch (GitHubApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict && attempt < 4)
            {
                // A just-created repo can take a moment before its branch / first commit shows up.
            }
            await delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
        }
    }
}
