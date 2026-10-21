using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;
using OptimusVibe.Sync;

namespace OptimusVibe.App.Services;

/// <summary>What the "Request presets" confirmation shows: counts and tokenized paths, never file contents.</summary>
public sealed record EvidencePreview(int Files, int Directories, int RegistryKeys, IReadOnlyList<string> Items, IReadOnlyList<string> Notes)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Files > 0) parts.Add(Files == 1 ? "1 settings file" : $"{Files} settings files");
            if (Directories > 0) parts.Add(Directories == 1 ? "1 folder listing" : $"{Directories} folder listings");
            if (RegistryKeys > 0) parts.Add(RegistryKeys == 1 ? "1 registry key" : $"{RegistryKeys} registry keys");
            return parts.Count == 0 ? "Only the game's name and install facts" : string.Join(", ", parts);
        }
    }

    /// <summary>The section headings that are not a file/folder/registry path ("Cloud check", "Current config", "Install: ...").</summary>
    public static EvidencePreview From(EvidenceBundle bundle)
    {
        var items = bundle.Sections.Select(s => s.Heading[4..]).ToList(); // "### File: {Documents}\x" -> "File: {Documents}\x"
        return new EvidencePreview(bundle.Count(EvidenceKind.File), bundle.Count(EvidenceKind.Directory), bundle.Count(EvidenceKind.Registry),
            items, bundle.Notes.ToList());
    }
}

/// <summary>
/// Everything the game-lab UI needs from Core in one place: finding the repo, the GitHub client, the evidence collector and the
/// background pass. All methods block or are async: call them off the UI thread. The OPTIMUSVIBE_LAB_DEMO switch (Debug builds only)
/// replaces GitHub with canned answers so the UI can be shown without touching any account.
/// </summary>
public sealed class GameLabService
{
    public sealed record Connection(GitHubApi Api, LabRepoRef Repo, AppSettings Settings);

#if DEBUG
    /// <summary>signedout | steps | ready (screenshots and UI tests; Debug builds only).</summary>
    public static string? Demo { get; } = Environment.GetEnvironmentVariable("OPTIMUSVIBE_LAB_DEMO")?.Trim().ToLowerInvariant();
#else
    public static string? Demo => null;
#endif

    public static bool IsDemo => Demo is { Length: > 0 };

    public (LabRepoRef? Repo, string? Problem) ResolveRepo(AppSettings settings, AppPaths? paths)
    {
        if (IsDemo) return (new LabRepoRef("demo-user/optimus-vibe-config", "main", "GitHub account"), null);
        return LabRepoResolver.Resolve(settings, paths);
    }

    public bool HasToken()
    {
        if (IsDemo) return Demo != "signedout";
        try { return GitHubAuth.LoadToken() is not null; }
        catch (NotImplementedException) { return false; }
    }

    /// <summary>The repo plus a GitHub client signed in as the user; null with the reason when the lab can't talk to GitHub.</summary>
    public Connection? Connect(AppSettings settings, AppPaths? paths, out string? problem)
    {
        var (repo, why) = ResolveRepo(settings, paths);
        if (repo is null) { problem = why; return null; }
        string? token = null;
        if (!IsDemo)
        {
            try { token = GitHubAuth.LoadToken(); }
            catch (NotImplementedException) { }
            if (token is null) { problem = "Sign in to GitHub first."; return null; }
        }
        problem = null;
        return new Connection(new GitHubApi(token ?? "demo"), repo, settings);
    }

    public static GameLabClient ClientFor(Connection c) => new(c.Api, c.Repo.Repo, c.Repo.Branch);

    /// <param name="verifyRun">The text of a "### Verify run" section (<see cref="OptimusVibe.Launch.RunResults.EvidenceText"/>), when the request carries a preset test.</param>
    public static EvidenceCollector CollectorFor(AppSession session, string? verifyRun = null) => new(new EvidenceOptions
    {
        CloudCheck = LabCloud.ForChecker(session.Store, session.Device),
        CurrentConfig = LabCloud.CurrentConfigFrom(session.Paths),
        VerifyRun = verifyRun is null ? null : _ => verifyRun,
    });

    public static EvidencePreview Preview(AppSession session, LabTarget target, string? verifyRun = null) =>
        EvidencePreview.From(CollectorFor(session, verifyRun).Collect(target));

    // ------------------------------------------------------------ wizard

    /// <summary>Read-only check of every setup step plus the requests. Never throws.</summary>
    public async Task<GameLabStatus> CheckAsync(Connection c, string? dataDir, CancellationToken ct = default)
    {
        if (IsDemo) return DemoStatus(c.Repo.Repo);
        var setup = new GameLabSetup(c.Api, c.Repo.Repo, c.Repo.Branch, new GameLabSetupOptions { DataDir = dataDir });
        var checks = await setup.CheckAllAsync(ct).ConfigureAwait(false);
        var steps = checks.Select(x => new StepStatus(x.Step.Name, x.Step.Title, x.Check.Done, x.Check.Skipped, x.Check.Detail, x.Check.Warning)).ToList();
        IReadOnlyList<LabIssue> issues = [];
        string? error = null;
        try { issues = await ClientFor(c).ListAsync(includeClosed: true, ct).ConfigureAwait(false); }
        catch (GitHubException ex) { error = ex.Message; }
        return new GameLabStatus(c.Repo.Repo, steps, issues.Where(i => i.IsOpen).ToList(), error);
    }

    public async Task<SetupResult> RunStepAsync(Connection c, string stepName, GameLabSetupOptions options, CancellationToken ct = default)
    {
        if (IsDemo) return new SetupResult(true, "Demo: nothing was changed.", Skipped: false);
        var setup = new GameLabSetup(c.Api, c.Repo.Repo, c.Repo.Branch, options);
        var step = setup.Step(stepName) ?? throw new ArgumentException($"Unknown step {stepName}");
        return await setup.RunStepAsync(step, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LabIssue>> ListIssuesAsync(Connection c, CancellationToken ct = default) =>
        IsDemo ? DemoIssues() : await ClientFor(c).ListAsync(includeClosed: true, ct).ConfigureAwait(false);

    public async Task<string> RunNowAsync(Connection c, CancellationToken ct = default) =>
        IsDemo ? "Demo: nothing is queued." : (await ClientFor(c).DrainQueueAsync(ct, retryFailed: true).ConfigureAwait(false)).Message;

    /// <summary>The project board's web address, or null when there is none (or the sign-in has no project scope).</summary>
    public async Task<string?> BoardUrlAsync(Connection c, CancellationToken ct = default)
    {
        if (IsDemo) return "https://github.com/users/demo-user/projects/1";
        try { return (await new LabBoard(c.Api, c.Repo.Repo).FindAsync(ct).ConfigureAwait(false))?.Url; }
        catch (Exception ex) when (ex is GitHubException or LabBoardScopeException) { return null; }
    }

    // ------------------------------------------------------------ requests

    public async Task<FiledRequest> FileRequestAsync(Connection c, AppSession session, LabKind kind, LabTarget target, CancellationToken ct = default)
    {
        if (IsDemo)
            return new FiledRequest(7, $"https://github.com/{c.Repo.Repo}/issues/7", false, 1, $"Demo: would file #7 ({GameLabContract.KindText(kind)})");
        return await ClientFor(c).FileRequestAsync(kind, target, session.Device.Name, CollectorFor(session), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Files the request, or - when GitHub can't be reached because we are offline - stores it (with the evidence as collected now) in
    /// the outbox to be sent later. An already open request is reused as before.
    /// </summary>
    public async Task<LabRequestOutcome> FileOrQueueAsync(Connection c, AppSession session, OutboxStore store, LabKind kind, LabTarget target,
        string? verifyRun = null, CancellationToken ct = default)
    {
        if (IsDemo) return new LabRequestOutcome(await FileRequestAsync(c, session, kind, target, ct).ConfigureAwait(false), null);
        return await OutboxFlow.FileLabRequestAsync(store, ClientFor(c), kind, target, session.Device.Name, CollectorFor(session, verifyRun), ct).ConfigureAwait(false);
    }

    /// <summary>A sync-check on the first configured game that is installed here, for the wizard's test step.</summary>
    public Func<CancellationToken, Task<string>> TestRequest(Connection c, AppSession session, IReadOnlyList<DetectedGame> detected) => async ct =>
    {
        foreach (var id in session.Store.GameIds())
        {
            GameDef def;
            try { def = session.Store.LoadGame(id); }
            catch (ConfigException) { continue; }
            var install = LibraryService.FindInstall(def, detected);
            if (install is null) continue;
            var filed = await FileRequestAsync(c, session, LabKind.SyncCheck, LabTarget.From(install, def), ct).ConfigureAwait(false);
            return $"Test request: {filed.Message} {filed.Url}";
        }
        return "No installed, configured game to test with; skipped.";
    };

    // ------------------------------------------------------------ background pass

    /// <summary>One background pass (see <see cref="LabCycle"/>). <paramref name="detected"/> may be empty: installs are scanned then.</summary>
    public async Task<LabCycleResult> RunCycleAsync(Connection c, AppSession session, IReadOnlyList<DetectedGame> detected, CancellationToken ct = default)
    {
        if (IsDemo) return new LabCycleResult(DemoIssues(), [], null, null, [], []);
        IReadOnlyList<DetectedGame>? scanned = null;
        IReadOnlyList<DetectedGame> Detected() => detected.Count > 0 ? detected : scanned ??= SafeScan(c.Settings);
        var games = new Lazy<List<GameDef>>(() => LoadGames(session.Store));

        LabTarget? TargetFor(LabIssue issue)
        {
            var r = issue.Request;
            if (r is null) return null;
            var install = Detected().FirstOrDefault(d => d.Key.Equals(r.Key, StringComparison.OrdinalIgnoreCase));
            var game = install is not null ? LibraryService.MatchGame(install, games.Value)
                : games.Value.FirstOrDefault(g => g.Id.Equals(r.GameId, StringComparison.OrdinalIgnoreCase));
            return new LabTarget
            {
                Name = r.Name, Store = r.Store, StoreId = r.StoreId, GameId = string.IsNullOrEmpty(r.GameId) ? game?.Id : r.GameId,
                InstallDir = install?.InstallDir, Exes = install?.Exes ?? [], Detected = install, Game = game,
            };
        }

        var deps = new LabCycleDeps
        {
            Client = ClientFor(c),
            TargetFor = TargetFor,
            Collector = () => CollectorFor(session),
            SyncBoard = ct2 => new BoardSync(c.Api, c.Repo.Repo, session.Paths.DataDir).SyncAsync(ct2),
            Paths = session.Paths,
            Provider = SyncProviders.For(session.Paths),
            State = new LabStateStore(session.Paths.DataDir),
        };
        return await LabCycle.RunAsync(deps, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<DetectedGame> SafeScan(AppSettings settings)
    {
        try { return GameScanners.ScanAll(settings.ScanStores); }
        catch (Exception ex)
        {
            Log.Warn($"game lab: scan failed: {ex.Message}");
            return [];
        }
    }

    private static List<GameDef> LoadGames(ConfigStore store)
    {
        var games = new List<GameDef>();
        foreach (var id in store.GameIds())
        {
            try { games.Add(store.LoadGame(id)); }
            catch (ConfigException ex) { Log.Warn($"game lab: skipped {id}: {ex.Message}"); }
        }
        return games;
    }

    /// <summary>Local-only sibling of <see cref="RunCycleAsync"/> for the local requests.yaml fallback.</summary>
    public static int QueueLocally(AppSession session, IReadOnlyList<LibraryEntry> entries) =>
        LibraryService.QueueRequests(session.Store, entries, session.Device.Name);

    // ------------------------------------------------------------ demo data (Debug builds)

    private static GameLabStatus DemoStatus(string repo)
    {
        bool Done(string name) => Demo switch
        {
            "ready" => true,
            "steps" => name is "scopes" or "labels" or "workflow",
            _ => false,
        };
        StepStatus S(string name, string title, string detail, bool skipped = false) =>
            new(name, title, Done(name), skipped && !Done(name), detail, null);
        var steps = new List<StepStatus>
        {
            S("scopes", "GitHub sign-in permissions", "Scopes: repo, workflow, project."),
            S("labels", "Request labels", "All 7 labels exist."),
            S("workflow", "Workflow and prompt files", "9 file(s) installed and current."),
            S("secret", "Claude token secret", "CLAUDE_CODE_OAUTH_TOKEN is set."),
            S("variables", "Limits (repo variables)", "GAME_LAB_DAILY_MAX=5, GAME_LAB_MODEL=claude-sonnet-5-5."),
            S("settings", "Repository settings", "Actions, workflow permissions and merge settings are right."),
            S("board", "Game-lab project board", "Project 'Optimus-Vibe game lab' is ready."),
            S("test", "Test request", "The lab has handled at least one request."),
        };
        return new GameLabStatus(repo, steps, DemoIssues().Where(i => i.IsOpen).ToList(), null);
    }

    private static IReadOnlyList<LabIssue> DemoIssues()
    {
        LabIssue I(int n, string name, string id, bool open, params string[] labels) => new(n, $"[game-lab] {name} - new",
            $"https://github.com/demo-user/optimus-vibe-config/issues/{n}", open, labels, GameLabContract.StatusFromLabels(labels),
            new GameLabContract.RequestInfo(LabKind.New, "", name, "steam", id, "desktop", "1.0"), null, null);
        return Demo == "ready"
            ? [I(3, "Hades II", "1145350", true, "lab:researching"), I(4, "Balatro", "2379780", true, "lab:queued"), I(5, "Hollow Knight", "367520", true, "lab:needs-evidence")]
            : [];
    }
}
