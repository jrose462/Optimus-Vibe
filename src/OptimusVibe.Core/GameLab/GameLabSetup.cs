using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.GameLab;

/// <summary>State of one setup step.</summary>
/// <param name="Done">Nothing left to do.</param>
/// <param name="Skipped">The step can't run with this sign-in (e.g. no 'project' scope) and is left out; not an error.</param>
/// <param name="Warning">Something worth showing even though the step is done.</param>
public sealed record SetupCheck(bool Done, string Detail, bool Skipped = false, string? Warning = null);

/// <summary>Outcome of running one step.</summary>
/// <param name="NeedsLogin">The token lacks scopes: the user must sign in to GitHub again (the step can't fix that itself).</param>
public sealed record SetupResult(bool Ok, string Message, bool Skipped = false, bool NeedsLogin = false);

/// <summary>One idempotent step of the guided game-lab setup.</summary>
public interface ISetupStep
{
    /// <summary>scopes | labels | workflow | secret | variables | settings | board | test</summary>
    string Name { get; }
    string Title { get; }

    /// <summary>Read-only: what is already in place. Never changes anything.</summary>
    Task<SetupCheck> CheckAsync(CancellationToken ct = default);

    /// <summary>Does what is missing (checks first; a done step is a no-op). Throws <see cref="GitHubException"/> on API failures.</summary>
    Task<SetupResult> RunAsync(CancellationToken ct = default);
}

public sealed class GameLabSetupOptions
{
    /// <summary>The Claude Code OAuth token to store as the repo secret (from `claude setup-token`). Never logged.</summary>
    public string? ClaudeToken { get; init; }

    /// <summary>Wanted GAME_LAB_DAILY_MAX / GAME_LAB_MODEL. Null keeps what the repo has, or the default when the variable is missing.</summary>
    public int? DailyMax { get; init; }
    public string? Model { get; init; }

    /// <summary>The "test" step: files a request (e.g. a sync-check for an already configured game) and returns a message. Null = step is skipped.</summary>
    public Func<CancellationToken, Task<string>>? RunTestRequest { get; init; }

    /// <summary>Local folder for the board's sync cache (AppPaths.DataDir). Null = no cache.</summary>
    public string? DataDir { get; init; }
}

/// <summary>
/// Guided setup backend: gets a private config repo ready to run the game lab. Every step has Check/Run and is safe to repeat.
/// <para>Fine-grained personal access tokens (the PAT path) need, on the config repo: Contents, Issues, Actions, Secrets, Variables,
/// Workflows (all read/write), plus Administration read/write for the repo-settings step. Projects need a classic token with the
/// <c>project</c> scope; without it the board step is skipped.</para>
/// </summary>
public sealed class GameLabSetup
{
    public const string FineGrainedPermissionsHelp =
        "Fine-grained token permissions on the config repo: Contents, Issues, Actions, Secrets, Variables and Workflows (read and write), " +
        "plus Administration (read and write) for the repository-settings step. The game-lab board needs a classic token with the 'project' scope.";

    public const string BoardTitle = "Optimus-Vibe game lab";

    private readonly GitHubApi _api;
    private readonly GameLabSetupOptions _o;
    private IReadOnlyList<string>? _scopes;
    private bool _scopesLoaded;

    public GameLabSetup(GitHubApi api, string repo, string branch = "main", GameLabSetupOptions? options = null)
    {
        if (!GitHubApi.IsValidRepoName(repo)) throw new ArgumentException($"'{repo}' is not a repo name — use owner/name.", nameof(repo));
        _api = api;
        Repo = repo;
        Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        _o = options ?? new GameLabSetupOptions();
        Steps =
        [
            new ScopesStep(this), new LabelsStep(this), new WorkflowStep(this), new SecretStep(this),
            new VariablesStep(this), new SettingsStep(this), new BoardStep(this), new TestStep(this),
        ];
    }

    public string Repo { get; }
    public string Branch { get; }
    public IReadOnlyList<ISetupStep> Steps { get; }

    public ISetupStep? Step(string name) => Steps.FirstOrDefault(s => s.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks every step; a check that fails (network, permissions) is reported as not done with the reason.</summary>
    public async Task<IReadOnlyList<(ISetupStep Step, SetupCheck Check)>> CheckAllAsync(CancellationToken ct = default)
    {
        var list = new List<(ISetupStep, SetupCheck)>();
        foreach (var s in Steps)
        {
            try { list.Add((s, await s.CheckAsync(ct).ConfigureAwait(false))); }
            catch (GitHubException ex) { list.Add((s, new SetupCheck(false, $"could not check: {ex.Message}"))); }
        }
        return list;
    }

    /// <summary>
    /// Runs the steps in order, stopping at the first failure that later steps depend on (scopes, workflow). A step that
    /// throws is reported as failed. Returns one result per step that was attempted.
    /// </summary>
    public async Task<IReadOnlyList<(ISetupStep Step, SetupResult Result)>> RunAllAsync(Action<ISetupStep, SetupResult>? progress = null, CancellationToken ct = default)
    {
        var list = new List<(ISetupStep, SetupResult)>();
        foreach (var s in Steps)
        {
            var r = await RunStepAsync(s, ct).ConfigureAwait(false);
            list.Add((s, r));
            progress?.Invoke(s, r);
            if (!r.Ok && s.Name is "scopes" or "workflow") break;
        }
        return list;
    }

    public async Task<SetupResult> RunStepAsync(ISetupStep step, CancellationToken ct = default)
    {
        try { return await step.RunAsync(ct).ConfigureAwait(false); }
        catch (GitHubException ex)
        {
            Log.Warn($"game lab setup step {step.Name} failed: {ex.Message}");
            return new SetupResult(false, ex.Message);
        }
    }

    /// <summary>Everything the UI shows: step states plus the request queue. Network errors degrade to empty lists with a summary.</summary>
    public async Task<GameLabStatus> GetStatusAsync(GameLabClient? client = null, CancellationToken ct = default)
    {
        var checks = await CheckAllAsync(ct).ConfigureAwait(false);
        var steps = checks.Select(c => new StepStatus(c.Step.Name, c.Step.Title, c.Check.Done, c.Check.Skipped, c.Check.Detail, c.Check.Warning)).ToList();
        IReadOnlyList<LabIssue> open = [];
        string? error = null;
        if (client is not null)
        {
            try { open = await client.ListAsync(false, ct).ConfigureAwait(false); }
            catch (GitHubException ex) { error = ex.Message; }
        }
        return GameLabStatus.Create(Repo, steps, open, error);
    }

    // ------------------------------------------------------------ shared helpers

    private string R => Repo;

    private async Task<IReadOnlyList<string>?> ScopesAsync(CancellationToken ct)
    {
        if (!_scopesLoaded)
        {
            _scopes = await _api.GetTokenScopesAsync(ct).ConfigureAwait(false);
            _scopesLoaded = true;
        }
        return _scopes;
    }

    private Task<GitHubResponse> Get(string path, CancellationToken ct) =>
        _api.RequestAsync(HttpMethod.Get, $"repos/{R}/{path}", null, R, false, ct);

    private Task<GitHubResponse> Send(HttpMethod m, string path, JsonNode? body, CancellationToken ct) =>
        _api.RequestAsync(m, $"repos/{R}/{path}", body, R, true, ct);

    // ------------------------------------------------------------ embedded files

    private const string SeedPrefix = "seed/config-repo/";

    /// <summary>Repo path (".github/workflows/game-lab.yml") → content, from seed\config-repo\** embedded in this build.</summary>
    public static IReadOnlyDictionary<string, byte[]> EmbeddedFiles()
    {
        var asm = typeof(GameLabSetup).Assembly;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(SeedPrefix, StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[name[SeedPrefix.Length..]] = ms.ToArray();
        }
        return files;
    }

    /// <summary>Files to put into the config repo: the embedded set (overridable for tests).</summary>
    internal Func<IReadOnlyDictionary<string, byte[]>> FileSource { get; set; } = EmbeddedFiles;

    // ------------------------------------------------------------ steps

    private sealed class ScopesStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "scopes";
        public string Title => "GitHub sign-in permissions";

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var granted = await s.ScopesAsync(ct).ConfigureAwait(false);
            if (granted is null)
                return new SetupCheck(true, "Fine-grained or app token: GitHub does not list its permissions.", Warning: FineGrainedPermissionsHelp);
            var missing = GitHubAuth.MissingScopes(granted);
            if (missing.Count > 0)
                return new SetupCheck(false, $"The sign-in lacks the {string.Join(" and ", missing.Select(m => $"'{m}'"))} scope. Sign in to GitHub again to grant it.");
            var optional = GitHubAuth.OptionalScopes.Where(o => !granted.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
            return new SetupCheck(true, $"Scopes: {string.Join(", ", granted)}.",
                Warning: optional.Count > 0 ? $"The '{optional[0]}' scope is missing, so the game-lab board is skipped. Sign in again to add it." : null);
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            var c = await CheckAsync(ct).ConfigureAwait(false);
            return c.Done ? new SetupResult(true, c.Detail) : new SetupResult(false, c.Detail, NeedsLogin: true);
        }
    }

    private sealed class LabelsStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "labels";
        public string Title => "Request labels";

        private async Task<List<string>> MissingAsync(CancellationToken ct)
        {
            var items = await s._api.GetAllAsync($"repos/{s.R}/labels?per_page=100", s.R, null, 10, ct).ConfigureAwait(false);
            var have = items.Select(i => i["name"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GameLabContract.Labels.Select(l => l.Name).Where(n => !have.Contains(n)).ToList();
        }

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var missing = await MissingAsync(ct).ConfigureAwait(false);
            return missing.Count == 0 ? new SetupCheck(true, "All 7 labels exist.") : new SetupCheck(false, $"Missing labels: {string.Join(", ", missing)}.");
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            var missing = await MissingAsync(ct).ConfigureAwait(false);
            foreach (var name in missing)
            {
                var (_, color, desc) = GameLabContract.Labels.First(l => l.Name == name);
                var r = await s._api.RequestAsync(HttpMethod.Post, $"repos/{s.R}/labels",
                    new JsonObject { ["name"] = name, ["color"] = color, ["description"] = desc }, s.R, false, ct).ConfigureAwait(false);
                if (!r.Ok && r.Status != HttpStatusCode.UnprocessableEntity) // 422 = it exists already
                    throw new GitHubApiException(r.Status, $"Could not create label {name}: {r.Json?["message"]?.GetValue<string>() ?? r.Status.ToString()}");
            }
            return new SetupResult(true, missing.Count == 0 ? "Labels already exist." : $"Created {missing.Count} label(s).");
        }
    }

    private sealed class WorkflowStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "workflow";
        public string Title => "Workflow and prompt files";

        private async Task<(string Head, string BaseTree, List<string> Changed)> DiffAsync(IReadOnlyDictionary<string, byte[]> files, CancellationToken ct)
        {
            var head = await s._api.GetBranchHeadAsync(s.R, s.Branch, ct).ConfigureAwait(false);
            var baseTree = await s._api.GetCommitTreeAsync(s.R, head, ct).ConfigureAwait(false);
            var tree = await s._api.GetTreeAsync(s.R, baseTree, recursive: true, ct).ConfigureAwait(false);
            var existing = tree.Entries.Where(e => e.Type == "blob").ToDictionary(e => e.Path, e => e.Sha, StringComparer.Ordinal);
            var changed = files.Where(f => existing.GetValueOrDefault(f.Key) != GitHubMirror.GitBlobSha(f.Value)).Select(f => f.Key).ToList();
            return (head, baseTree, changed);
        }

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var files = s.FileSource();
            if (files.Count == 0) return new SetupCheck(false, "This build has no game-lab files embedded (seed\\config-repo).");
            var (_, _, changed) = await DiffAsync(files, ct).ConfigureAwait(false);
            return changed.Count == 0
                ? new SetupCheck(true, $"{files.Count} file(s) installed and current.")
                : new SetupCheck(false, $"{changed.Count} of {files.Count} file(s) missing or outdated: {string.Join(", ", changed.Take(4))}{(changed.Count > 4 ? ", ..." : "")}.");
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            var files = s.FileSource();
            if (files.Count == 0) return new SetupResult(false, "This build has no game-lab files embedded (seed\\config-repo).");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var (head, baseTree, changed) = await DiffAsync(files, ct).ConfigureAwait(false);
                if (changed.Count == 0) return new SetupResult(true, "Workflow and prompt files already installed.");
                try
                {
                    var entries = new List<GitTreeChange>();
                    foreach (var path in changed)
                        entries.Add(new GitTreeChange(path, await s._api.CreateBlobAsync(s.R, files[path], ct).ConfigureAwait(false)));
                    var tree = await s._api.CreateTreeAsync(s.R, baseTree, entries, ct).ConfigureAwait(false);
                    var commit = await s._api.CreateCommitAsync(s.R, "Install Optimus-Vibe game lab", tree, head, ct).ConfigureAwait(false);
                    if (await s._api.UpdateRefAsync(s.R, s.Branch, commit, ct).ConfigureAwait(false))
                    {
                        Log.Info($"game lab: installed {changed.Count} file(s) into {s.R}");
                        return new SetupResult(true, $"Installed {changed.Count} file(s) in one commit.");
                    }
                }
                catch (GitHubApiException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity
                                                    && changed.Any(c => c.StartsWith(".github/workflows/", StringComparison.Ordinal)))
                {
                    var scopes = await s.ScopesAsync(ct).ConfigureAwait(false);
                    if (GitHubAuth.MissingScopes(scopes).Contains("workflow"))
                        return new SetupResult(false, "GitHub refuses workflow files without the 'workflow' scope. Sign in to GitHub again to grant it.", NeedsLogin: true);
                    throw new GitHubApiException(ex.Status, $"GitHub refused the workflow files: {ex.Message} (a fine-grained token needs the Workflows read/write permission).", ex);
                }
                Log.Info($"game lab: {s.Branch} moved while installing the workflow (attempt {attempt}); retrying");
            }
            return new SetupResult(false, $"{s.Repo} ({s.Branch}) kept changing while installing; try again in a moment.");
        }
    }

    private sealed class SecretStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "secret";
        public string Title => "Claude token secret";

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var r = await s.Get($"actions/secrets/{GameLabContract.SecretName}", ct).ConfigureAwait(false);
            if (r.Ok) return new SetupCheck(true, $"{GameLabContract.SecretName} is set (GitHub never shows its value).");
            if (r.Status == HttpStatusCode.NotFound) return new SetupCheck(false, $"{GameLabContract.SecretName} is not set yet.");
            throw LabHttp.Error(r, s.R);
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            var token = s._o.ClaudeToken?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                var c = await CheckAsync(ct).ConfigureAwait(false);
                return c.Done
                    ? new SetupResult(true, "Secret already set (no new token given).")
                    : new SetupResult(false, "No Claude token given. Run `claude setup-token` and pass the result (--claude-token or CLAUDE_CODE_OAUTH_TOKEN).");
            }

            var key = await s.Get("actions/secrets/public-key", ct).ConfigureAwait(false);
            if (!key.Ok) throw LabHttp.Error(key, s.R);
            var keyId = key.Json?["key_id"]?.GetValue<string>();
            var publicKey = key.Json?["key"]?.GetValue<string>();
            if (keyId is null || publicKey is null) throw new GitHubApiException(null, "GitHub returned no public key for the repository secrets.");

            var encrypted = SealedBox.Encrypt(token, publicKey); // the token itself is never logged
            await s.Send(HttpMethod.Put, $"actions/secrets/{GameLabContract.SecretName}",
                new JsonObject { ["encrypted_value"] = encrypted, ["key_id"] = keyId }, ct).ConfigureAwait(false);
            Log.Info($"game lab: set repo secret {GameLabContract.SecretName} on {s.R}");
            return new SetupResult(true, $"Stored {GameLabContract.SecretName} (encrypted for GitHub).");
        }
    }

    private sealed class VariablesStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "variables";
        public string Title => "Limits (repo variables)";

        private async Task<Dictionary<string, string?>> CurrentAsync(CancellationToken ct)
        {
            var result = new Dictionary<string, string?>();
            foreach (var name in new[] { GameLabContract.DailyMaxVariable, GameLabContract.ModelVariable })
            {
                var r = await s.Get($"actions/variables/{name}", ct).ConfigureAwait(false);
                result[name] = r.Ok ? r.Json?["value"]?.GetValue<string>() ?? "" : null;
                if (!r.Ok && r.Status != HttpStatusCode.NotFound) throw LabHttp.Error(r, s.R);
            }
            return result;
        }

        private Dictionary<string, string> Wanted(Dictionary<string, string?> current) => new()
        {
            [GameLabContract.DailyMaxVariable] = s._o.DailyMax?.ToString() ?? current[GameLabContract.DailyMaxVariable] ?? GameLabContract.DefaultDailyMax.ToString(),
            [GameLabContract.ModelVariable] = string.IsNullOrWhiteSpace(s._o.Model) ? current[GameLabContract.ModelVariable] ?? GameLabContract.DefaultModel : s._o.Model.Trim(),
        };

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var cur = await CurrentAsync(ct).ConfigureAwait(false);
            var todo = Wanted(cur).Where(w => cur[w.Key] != w.Value).Select(w => w.Key).ToList();
            return todo.Count == 0
                ? new SetupCheck(true, $"{GameLabContract.DailyMaxVariable}={cur[GameLabContract.DailyMaxVariable]}, {GameLabContract.ModelVariable}={cur[GameLabContract.ModelVariable]}.")
                : new SetupCheck(false, $"To set: {string.Join(", ", todo)}.");
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            var cur = await CurrentAsync(ct).ConfigureAwait(false);
            var changed = new List<string>();
            foreach (var (name, value) in Wanted(cur))
            {
                if (cur[name] == value) continue;
                if (cur[name] is null)
                    await s.Send(HttpMethod.Post, "actions/variables", new JsonObject { ["name"] = name, ["value"] = value }, ct).ConfigureAwait(false);
                else
                    await s.Send(HttpMethod.Patch, $"actions/variables/{name}", new JsonObject { ["name"] = name, ["value"] = value }, ct).ConfigureAwait(false);
                changed.Add($"{name}={value}");
            }
            return new SetupResult(true, changed.Count == 0 ? "Variables already set." : $"Set {string.Join(", ", changed)}.");
        }
    }

    private sealed class SettingsStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "settings";
        public string Title => "Repository settings";

        private sealed record State(bool ActionsEnabled, string? WorkflowPermissions, bool CanApprove, bool Squash, bool DeleteBranch, bool AutoMerge)
        {
            public bool ActionsOk => ActionsEnabled;
            public bool WorkflowOk => WorkflowPermissions == "read" && CanApprove;
            public bool RepoOk => Squash && DeleteBranch && AutoMerge;
        }

        private async Task<State> ReadAsync(CancellationToken ct)
        {
            var repo = await s._api.RequestAsync(HttpMethod.Get, $"repos/{s.R}", null, s.R, true, ct).ConfigureAwait(false);
            var actions = await s.Get("actions/permissions", ct).ConfigureAwait(false);
            if (!actions.Ok) throw LabHttp.Error(actions, s.R);
            var wf = await s.Get("actions/permissions/workflow", ct).ConfigureAwait(false);
            if (!wf.Ok) throw LabHttp.Error(wf, s.R);
            bool B(JsonNode? n) { try { return n?.GetValue<bool>() ?? false; } catch (InvalidOperationException) { return false; } }
            return new State(
                B(actions.Json?["enabled"]),
                wf.Json?["default_workflow_permissions"]?.GetValue<string>(),
                B(wf.Json?["can_approve_pull_request_reviews"]),
                B(repo.Json?["allow_squash_merge"]), B(repo.Json?["delete_branch_on_merge"]), B(repo.Json?["allow_auto_merge"]));
        }

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var st = await ReadAsync(ct).ConfigureAwait(false);
            var todo = new List<string>();
            if (!st.ActionsOk) todo.Add("enable Actions");
            if (st.WorkflowPermissions != "read") todo.Add("workflow permissions: read");
            if (!st.CanApprove) todo.Add("let Actions create and approve pull requests");
            if (!st.Squash) todo.Add("allow squash merge");
            if (!st.DeleteBranch) todo.Add("delete branch on merge");
            if (!st.AutoMerge) todo.Add("allow auto-merge");
            return todo.Count == 0 ? new SetupCheck(true, "Actions, workflow permissions and merge settings are right.") : new SetupCheck(false, "To change: " + string.Join("; ", todo) + ".");
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            var st = await ReadAsync(ct).ConfigureAwait(false);
            var changed = new List<string>();
            if (!st.ActionsOk)
            {
                await s.Send(HttpMethod.Put, "actions/permissions", new JsonObject { ["enabled"] = true }, ct).ConfigureAwait(false);
                changed.Add("Actions enabled");
            }
            if (!st.WorkflowOk)
            {
                await s.Send(HttpMethod.Put, "actions/permissions/workflow",
                    new JsonObject { ["default_workflow_permissions"] = "read", ["can_approve_pull_request_reviews"] = true }, ct).ConfigureAwait(false);
                changed.Add("workflow permissions");
            }
            if (!st.RepoOk)
            {
                await s._api.RequestAsync(HttpMethod.Patch, $"repos/{s.R}",
                    new JsonObject { ["allow_squash_merge"] = true, ["delete_branch_on_merge"] = true, ["allow_auto_merge"] = true }, s.R, true, ct).ConfigureAwait(false);
                changed.Add("merge settings");
            }
            return new SetupResult(true, changed.Count == 0 ? "Repository settings already right." : "Updated " + string.Join(", ", changed) + ".");
        }
    }

    private sealed class BoardStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "board";
        public string Title => "Game-lab project board";

        private async Task<bool> HasProjectScopeAsync(CancellationToken ct)
        {
            var scopes = await s.ScopesAsync(ct).ConfigureAwait(false);
            return scopes is null || scopes.Contains("project", StringComparer.OrdinalIgnoreCase); // fine-grained: try and see
        }

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            if (!await HasProjectScopeAsync(ct).ConfigureAwait(false))
                return new SetupCheck(false, "Skipped: the sign-in has no 'project' scope (sign in again to add it). The lab works without the board.", Skipped: true);
            try
            {
                var board = new LabBoard(s._api, s.Repo);
                var info = await board.FindAsync(ct).ConfigureAwait(false);
                if (info is null) return new SetupCheck(false, $"The project '{BoardTitle}' does not exist yet.");
                if (!info.Linked) return new SetupCheck(false, $"The project exists ({info.Url}) but is not linked to {s.Repo}.");
                if (!info.StatusReady) return new SetupCheck(false, "The project exists but its status options are not set up.");
                return new SetupCheck(true, $"Project '{BoardTitle}' is ready: {info.Url}");
            }
            catch (LabBoardScopeException ex)
            {
                return new SetupCheck(false, "Skipped: " + ex.Message, Skipped: true);
            }
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            if (!await HasProjectScopeAsync(ct).ConfigureAwait(false))
                return new SetupResult(true, "Skipped the board: the sign-in has no 'project' scope. Sign in again to add it; the lab works without it.", Skipped: true);
            try
            {
                var info = await new LabBoard(s._api, s.Repo).EnsureAsync(ct).ConfigureAwait(false);
                return new SetupResult(true, $"Board ready: {info.Url}");
            }
            catch (LabBoardScopeException ex)
            {
                return new SetupResult(true, "Skipped the board: " + ex.Message, Skipped: true);
            }
        }
    }

    private sealed class TestStep(GameLabSetup s) : ISetupStep
    {
        public string Name => "test";
        public string Title => "Test request";

        public async Task<SetupCheck> CheckAsync(CancellationToken ct = default)
        {
            var r = await s._api.RequestAsync(HttpMethod.Get,
                $"repos/{s.R}/issues?labels={Uri.EscapeDataString(GameLabContract.LabelGameLab)}&state=all&per_page=1", null, s.R, false, ct).ConfigureAwait(false);
            if (!r.Ok) return new SetupCheck(false, "Could not look for earlier requests.");
            return r.Json is JsonArray { Count: > 0 }
                ? new SetupCheck(true, "The lab has handled at least one request.")
                : new SetupCheck(false, "No request has been filed yet.", Skipped: s._o.RunTestRequest is null);
        }

        public async Task<SetupResult> RunAsync(CancellationToken ct = default)
        {
            if (s._o.RunTestRequest is null) return new SetupResult(true, "No test request available (needs a configured game); skipped.", Skipped: true);
            return new SetupResult(true, await s._o.RunTestRequest(ct).ConfigureAwait(false));
        }
    }
}

/// <summary>One step as the UI shows it.</summary>
public sealed record StepStatus(string Name, string Title, bool Done, bool Skipped, string Detail, string? Warning);

/// <summary>Setup state plus the request queue, for the app and `lab status`.</summary>
public sealed record GameLabStatus(string Repo, IReadOnlyList<StepStatus> Steps, IReadOnlyList<LabIssue> Open, string? Error)
{
    /// <summary>Every non-skipped step is done: requests can be filed.</summary>
    public bool SetupComplete => Steps.All(s => s.Done || s.Skipped);

    /// <summary>Everything except the optional steps (board, test) is done.</summary>
    public bool Ready => Steps.Where(s => s.Name is "scopes" or "labels" or "workflow" or "secret" or "variables" or "settings").All(s => s.Done);

    public int Count(LabStatus s) => Open.Count(i => i.Status == s);
    public int Queued => Count(LabStatus.Queued);
    public int Researching => Count(LabStatus.Researching);
    public int NeedsEvidence => Count(LabStatus.NeedsEvidence);
    public int InReview => Count(LabStatus.Review);

    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            var todo = Steps.Where(s => !s.Done && !s.Skipped).Select(s => s.Title).ToList();
            sb.Append(Ready ? "Game lab is ready" : $"Game lab setup incomplete ({string.Join(", ", todo)})");
            if (Open.Count > 0)
                sb.Append($" - {Open.Count} open request(s): {Queued} queued, {Researching} researching, {NeedsEvidence} need evidence, {InReview} in review");
            if (Error is not null) sb.Append(" - could not read requests: ").Append(Error);
            return sb.ToString();
        }
    }

    internal static GameLabStatus Create(string repo, IReadOnlyList<StepStatus> steps, IReadOnlyList<LabIssue> open, string? error) =>
        new(repo, steps, open, error);
}
