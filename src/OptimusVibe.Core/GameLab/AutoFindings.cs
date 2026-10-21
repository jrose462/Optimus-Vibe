using OptimusVibe.CloudSync;
using OptimusVibe.Launch;
using OptimusVibe.Library;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;

namespace OptimusVibe.GameLab;

/// <summary>Why the app would ask the game lab to look at a game again by itself.</summary>
public enum AutoFindingReason
{
    /// <summary>A new store build is installed, and the presets were known to work on the build before it.</summary>
    GameUpdated,

    /// <summary>Two Test preset runs in a row found the game overrode the preset.</summary>
    TestsOverridden,

    /// <summary>The game reset the preset's settings after normal launches on two separate days.</summary>
    KeepsResetting,

    /// <summary>The local cloud-sync check still can't tell after enough clean launches, while something changes the settings.</summary>
    SyncUnsure,
}

/// <summary>A finding worth sending to the game lab: the request kind and why.</summary>
public sealed record AutoFinding(LabKind Kind, AutoFindingReason Reason)
{
    /// <summary>One short clause for logs and toasts ("the game updated after the presets worked").</summary>
    public string Text => Reason switch
    {
        AutoFindingReason.GameUpdated => "the game updated after the presets worked",
        AutoFindingReason.TestsOverridden => "the game overrode the preset in two tests in a row",
        AutoFindingReason.KeepsResetting => "the game kept resetting the preset's settings",
        _ => "something keeps changing the settings and the cloud-sync check can't tell why",
    };
}

/// <summary>One managed, installed game the app may file a finding for, with its health inputs on this device.</summary>
/// <param name="Key">"store:id" of the install (<see cref="LabTarget.Key"/>).</param>
public sealed record AutoFindingCandidate(string Key, string GameId, string Name, HealthInputs Inputs, bool OptedOut = false);

/// <summary>A finding that was sent (or saved to the outbox, <see cref="Queued"/>), or that found an open request to reuse (<see cref="Reused"/>).</summary>
public sealed record AutoFindingFiled(string GameId, string Name, AutoFinding Finding, int? Issue, string? Url, bool Reused, bool Queued);

/// <param name="Skipped">Game id → why a finding was not sent (open request, rate limit, outbox, cap).</param>
public sealed record AutoFindingsResult(IReadOnlyList<AutoFindingFiled> Filed, IReadOnlyDictionary<string, string> Skipped, IReadOnlyList<string> Errors)
{
    public static readonly AutoFindingsResult None = new([], new Dictionary<string, string>(), []);
}

/// <summary>How many automatic findings may still be sent in this app run (<see cref="AutoFindings.MaxPerStart"/>). Thread-safe.</summary>
public sealed class AutoFindingBudget(int max)
{
    private int _left = max;

    public int Remaining => Volatile.Read(ref _left);

    /// <summary>Takes one; false when none is left.</summary>
    public bool TryTake()
    {
        while (true)
        {
            var left = Volatile.Read(ref _left);
            if (left <= 0) return false;
            if (Interlocked.CompareExchange(ref _left, left - 1, left) == left) return true;
        }
    }
}

/// <summary>
/// Consent-gated automatic lab findings ("Send findings to the game lab automatically", off by default): when a game updated after its presets
/// worked, or keeps overriding them, ask the lab to research it again (Refresh); when the local cloud-sync check stays unsure, ask it once to
/// re-check sync. Never launches anything and never flags a game. Limits: one automatic Refresh per game every <see cref="RefreshMemory"/>,
/// one automatic sync re-check per game ever, at most <see cref="MaxPerStart"/> per app start, never while a request for the game is open
/// or waiting in the outbox, never within <see cref="LabRouter.RequestMemory"/> of any request for it, never for a flagged or opted-out game.
/// The workflow's daily cap still applies on the lab's side. <see cref="Detect"/> is pure; <see cref="RunAsync"/> files through a delegate.
/// </summary>
public static class AutoFindings
{
    /// <summary>At most one automatic Refresh per game in this window.</summary>
    public static readonly TimeSpan RefreshMemory = TimeSpan.FromDays(14);

    /// <summary>At most this many automatic findings per app start.</summary>
    public const int MaxPerStart = 2;

    /// <summary>Passively overridden launches must fall on at least this many separate (UTC) days.</summary>
    public const int SeparateDays = 2;

    public const string WatchSessionsTitle = "Let Optimus-Vibe check presets after each session";
    public const string WatchSessionsText =
        "After you play, Optimus-Vibe reads the game's settings files and tells you when the game changed the preset. Nothing leaves this PC and nothing is launched.";

    public const string SettingTitle = "Send findings to the game lab automatically";

    /// <summary>The setting's description; <paramref name="repo"/> names the private config repo when known.</summary>
    public static string SettingText(string? repo) =>
        "When a game updates or keeps resetting your presets, ask the game lab to look again. At most one request per game every 2 weeks. " +
        $"Evidence goes only to your private config repo{(repo is { Length: > 0 } r ? $" ({r})" : "")}.";

    /// <summary>The dim line of the post-session card that asks for consent once.</summary>
    public const string ConsentQuestion = "Send findings like this to the game lab by itself from now on? At most one request per game every 2 weeks.";

    /// <summary>
    /// What the evidence on this device says is worth sending to the lab for a game, ignoring consent and the limits; null when nothing is.
    /// In order: a store build update after the presets worked, two overridden tests in a row, passive resets on two separate days (all Refresh),
    /// then cloud sync still unsure (SyncCheck). Only evidence about the current preset counts (<see cref="GameHealth.CountsForCurrentPreset"/>);
    /// a flagged game, or one without presets for this device, never has a finding.
    /// </summary>
    public static AutoFinding? Detect(HealthInputs i)
    {
        if (i.Flag is not null || !i.HasConfig || !i.HasPresets) return null;
        // an exe fingerprint can change when a launcher touches the exe: it only ever suggests, never files by itself
        if (i.GameUpdatedByStore && HeldOn(i, i.BaselineBuild)) return new AutoFinding(LabKind.Refresh, AutoFindingReason.GameUpdated);
        if (GameFlags.SuggestsFromVerify(i.OverriddenStreak) && GameHealth.VerifyStreakStands(i) && i.LastVerify is { } v &&
            RunResults.Verdict(v) == RunVerdict.Overridden)
            return new AutoFinding(LabKind.Refresh, AutoFindingReason.TestsOverridden);
        if (ResetDays(i) >= SeparateDays) return new AutoFinding(LabKind.Refresh, AutoFindingReason.KeepsResetting);
        if (SyncStillUnsure(i.CloudSync)) return new AutoFinding(LabKind.SyncCheck, AutoFindingReason.SyncUnsure);
        return null;
    }

    /// <summary>
    /// Why a finding for <paramref name="c"/> is not sent now, or null when it may be: flagged or opted out, a request open for the game, one
    /// waiting in the outbox (<paramref name="queued"/>, from <see cref="QueuedKeys"/>), any request within <see cref="LabRouter.RequestMemory"/>,
    /// an automatic Refresh within <see cref="RefreshMemory"/>, or an automatic sync re-check ever.
    /// </summary>
    public static string? SkipReason(AutoFindingCandidate c, AutoFinding f, LabState state, IReadOnlySet<string> queued, DateTime nowUtc)
    {
        if (c.OptedOut || c.Inputs.Flag is not null) return "not managed";
        if (c.Inputs.LabRequest is { IsOpen: true } open) return $"request #{open.Number} is open";
        if (queued.Contains(c.Key) || queued.Contains(c.GameId)) return "a request is waiting in the outbox";
        if (state.Requested.TryGetValue(c.Key, out var asked) && nowUtc - asked < LabRouter.RequestMemory) return "requested recently";
        if (f.Kind == LabKind.SyncCheck)
            return state.AutoSyncCheck.ContainsKey(c.GameId) ? "sync was re-checked automatically before" : null;
        return state.AutoRefresh.TryGetValue(c.GameId, out var last) && nowUtc - last < RefreshMemory ? "refreshed automatically in the last 2 weeks" : null;
    }

    /// <summary>The "store:id" keys and game ids of every lab request waiting in the outbox (any kind): those games are not asked about again.</summary>
    public static IReadOnlySet<string> QueuedKeys(IEnumerable<OutboxItem> items)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Kind != OutboxKind.LabRequest) continue;
            try
            {
                var r = item.LabRequest;
                if (r.Store.Length > 0 && r.StoreId.Length > 0) keys.Add($"{r.Store}:{r.StoreId}");
                if (r.GameId.Length > 0) keys.Add(r.GameId);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
            {
                Log.Warn($"outbox: could not read lab request {item.Id}: {ex.Message}");
            }
        }
        return keys;
    }

    /// <summary>
    /// Sends the findings of <paramref name="candidates"/> when <paramref name="consent"/> is on and the lab is ready: each one through
    /// <paramref name="file"/> (which files it, reuses an open request, or saves it to the outbox when offline), within the limits of
    /// <see cref="SkipReason"/> and <paramref name="budget"/>. Remembers each one in <paramref name="state"/> (also a reused or queued one), so it
    /// is not asked again. Consent off sends nothing. Never throws except on cancellation.
    /// </summary>
    public static async Task<AutoFindingsResult> RunAsync(IReadOnlyList<AutoFindingCandidate> candidates, bool consent, bool labReady,
        AutoFindingBudget budget, Func<AutoFindingCandidate, AutoFinding, CancellationToken, Task<LabRequestOutcome>> file, LabStateStore state,
        IReadOnlySet<string>? queued = null, Func<DateTime>? now = null, CancellationToken ct = default)
    {
        if (!consent || !labReady || candidates.Count == 0) return AutoFindingsResult.None;
        now ??= () => DateTime.UtcNow;
        queued ??= new HashSet<string>();
        var filed = new List<AutoFindingFiled>();
        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            if (!done.Add(c.GameId)) continue; // one finding per game per pass
            AutoFinding? f;
            try { f = Detect(c.Inputs); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"game lab: could not judge {c.Name}: {ex.Message}");
                continue;
            }
            if (f is null) continue;

            // read the state again for every game: another pass (or the user) may have filed meanwhile
            if (state.TryLoad() is not { } s)
            {
                skipped[c.GameId] = "the lab state could not be read";
                continue;
            }
            var at = now();
            if (SkipReason(c, f, s, queued, at) is { } why)
            {
                skipped[c.GameId] = why;
                continue;
            }
            if (!budget.TryTake())
            {
                skipped[c.GameId] = $"at most {MaxPerStart} automatic requests per start";
                continue;
            }
            try
            {
                var outcome = await file(c, f, ct).ConfigureAwait(false);
                var saved = state.Update(st =>
                {
                    st.Requested[c.Key] = at;
                    if (f.Kind == LabKind.SyncCheck) st.AutoSyncCheck[c.GameId] = at;
                    else st.AutoRefresh[c.GameId] = at;
                });
                if (!saved) Log.Warn($"game lab: could not remember the automatic request for {c.Name}");
                Log.Info($"game lab: automatic {GameLabContract.KindText(f.Kind)} for {c.Name} ({f.Text}): " +
                         (outcome.Filed is { } r ? $"#{r.Number}{(r.Reused ? " (already open)" : "")}" : "saved to the outbox"));
                filed.Add(new AutoFindingFiled(c.GameId, c.Name, f, outcome.Filed?.Number, outcome.Filed?.Url, outcome.Filed?.Reused == true, outcome.WasQueued));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"game lab: automatic request for {c.Name} failed: {ex.Message}");
                errors.Add($"{c.Name}: {ex.Message}");
            }
        }
        return new AutoFindingsResult(filed, skipped, errors);
    }

    /// <summary>A held Test preset or a passively held launch of the current preset was on <paramref name="build"/>.</summary>
    private static bool HeldOn(HealthInputs i, string? build)
    {
        if (string.IsNullOrEmpty(build)) return false;
        bool On(RunResult r) => string.Equals(r.Build, build, StringComparison.OrdinalIgnoreCase) && GameHealth.CountsForCurrentPreset(i, r);
        if (i.LastVerify is { } v && RunResults.Verdict(v) == RunVerdict.Held && On(v)) return true;
        var verdicts = i.PassiveVerdicts;
        for (var k = 0; k < i.Recent.Count; k++)
            if (verdicts[k] == PassiveVerdict.Held && On(i.Recent[k])) return true;
        return false;
    }

    /// <summary>
    /// The separate (UTC) days of the current preset's passively overridden launches, newest back (the rules of
    /// <see cref="GameHealth.PassiveOverridden"/>: a held launch ends the count, launches from before the presets arrived don't count).
    /// </summary>
    private static int ResetDays(HealthInputs i)
    {
        var verdicts = i.PassiveVerdicts;
        var days = new HashSet<DateTime>();
        for (var k = i.Recent.Count - 1; k >= 0; k--)
        {
            var r = i.Recent[k];
            if (i.Presets is { } made && r.Utc < made.MadeUtc) break;
            if (!GameHealth.CountsForCurrentPreset(i, r)) continue;
            if (verdicts[k] == PassiveVerdict.Held) break;
            if (verdicts[k] == PassiveVerdict.Overridden) days.Add(r.Utc.Date);
        }
        return days.Count;
    }

    /// <summary>
    /// Something doubts the settings' sync (a contradiction, or changes made while Optimus-Vibe wasn't running) and the local check is still at
    /// low confidence or less after <see cref="CloudSyncVerdict.CleanLaunchesForNotSynced"/> clean launches: the free local check won't settle it.
    /// </summary>
    public static bool SyncStillUnsure(CloudSyncReport? r) =>
        r is not null && (r.Contradiction is not null || r.HasExternalChanges) && r.Confidence <= SyncConfidence.Low &&
        r.CleanLaunches >= CloudSyncVerdict.CleanLaunchesForNotSynced;
}
