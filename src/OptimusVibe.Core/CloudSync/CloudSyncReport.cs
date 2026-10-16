namespace OptimusVibe.CloudSync;

/// <summary>What the evidence says about whether a game's graphics settings travel between machines.</summary>
public enum SyncVerdict { Unknown, Synced, NotSynced }

public enum SyncConfidence { None, Low, Medium, High }

/// <summary>
/// The verdict for one game on this device: the researched claim (<see cref="Declared"/>), what launcher logs and
/// observed behaviour say (<see cref="Detected"/>), the evidence behind it, and what to do about it.
/// </summary>
public sealed record CloudSyncReport
{
    public required string GameId { get; init; }
    public required string Device { get; init; }

    /// <summary>GameDef.CloudSyncsSettings: true = settings sync, false = per machine, null = not researched.</summary>
    public bool? Declared { get; init; }

    public SyncVerdict Detected { get; init; } = SyncVerdict.Unknown;
    public SyncConfidence Confidence { get; init; } = SyncConfidence.None;

    /// <summary>Human-readable lines, strongest first.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Set when the researched claim and the detected verdict disagree with decent (medium+) confidence.</summary>
    public string? Contradiction { get; init; }

    /// <summary>Offer "you can safely opt out". Never applied automatically.</summary>
    public bool SuggestOptOut { get; init; }

    /// <summary>The settings changed while Optimus-Vibe was not running (any time since observing began).</summary>
    public bool HasExternalChanges { get; init; }

    public int CleanLaunches { get; init; }

    /// <summary>Strong observation: settings arrived matching another device's preset. Null when none.</summary>
    public ExternalChangeRecord? StrongChange { get; init; }

    public bool HasContradiction => Contradiction is not null;

    /// <summary>Declared and detected folded into one answer: true = settings travel, false = per machine, null = unknown.</summary>
    public bool? Effective =>
        Detected switch
        {
            SyncVerdict.Synced => true,
            SyncVerdict.NotSynced when Confidence >= SyncConfidence.Medium || Declared != true => false,
            _ => Declared,
        };

    /// <summary>Short verdict for a title line.</summary>
    public string Headline => Effective switch
    {
        true => "Settings sync between your devices",
        false => "Settings stay on this device",
        _ => "Not known yet",
    };

    /// <summary>"high confidence", "low confidence"; empty when nothing is detected.</summary>
    public string ConfidenceText => Confidence == SyncConfidence.None ? "" : $"{Confidence.ToString().ToLowerInvariant()} confidence";
}

/// <summary>One time the game's settings changed while Optimus-Vibe was not running.</summary>
public sealed class ExternalChangeRecord
{
    public DateTime Utc { get; set; }
    public List<string> Files { get; set; } = new();

    /// <summary>Set when the new values match another device's preset or save ("ally-x").</summary>
    public string? FromDevice { get; set; }

    /// <summary>Label of that preset ("Performance", or a manual save's name).</summary>
    public string? Preset { get; set; }

    /// <summary>Keys compared / matching (key-based presets); a whole-file snapshot match is 1/1.</summary>
    public int Compared { get; set; }
    public int Matched { get; set; }

    public bool Strong => FromDevice is not null;
}

/// <summary>Result of looking at one launcher's own records.</summary>
public sealed class LauncherEvidence
{
    public SyncVerdict Verdict { get; init; } = SyncVerdict.Unknown;
    public SyncConfidence Confidence { get; init; } = SyncConfidence.None;
    public List<string> Lines { get; init; } = new();

    public static LauncherEvidence Unknown(params string[] lines) => new() { Lines = [.. lines] };
}

/// <summary>What the observation store knows about a game on this device.</summary>
public sealed class ObservationSummary
{
    public int CleanLaunches { get; init; }
    public IReadOnlyList<ExternalChangeRecord> Changes { get; init; } = [];

    public bool HasChanges => Changes.Count > 0;
    public ExternalChangeRecord? LatestStrong => Changes.Where(c => c.Strong).OrderByDescending(c => c.Utc).FirstOrDefault();
}

/// <summary>Turns declared + launcher + observed evidence into one <see cref="CloudSyncReport"/>.</summary>
public static class CloudSyncVerdict
{
    /// <summary>Clean launches needed before "never changed" counts as evidence of per-machine settings.</summary>
    public const int CleanLaunchesForNotSynced = 5;
    public const int CleanLaunchesForMedium = 10;

    /// <summary>
    /// Rules: a strong observation (settings arrived matching another device's preset) is Synced/High and overrides a
    /// launcher's "not synced". Otherwise any syncing launcher gives Synced, else a launcher that lists only saves gives
    /// NotSynced (one step less confident if the settings changed anyway). With no launcher answer, external changes
    /// leave it Unknown; 5+ clean launches alone give NotSynced/Low (10+: Medium). Opt-out is suggested only when the
    /// effective answer is "per machine" and nothing ever changed the settings behind our back.
    /// </summary>
    public static CloudSyncReport Combine(string gameId, string device, bool? declared,
        IReadOnlyList<LauncherEvidence> launchers, ObservationSummary observed)
    {
        var evidence = new List<string>();
        var verdict = SyncVerdict.Unknown;
        var confidence = SyncConfidence.None;

        var syncing = launchers.Where(l => l.Verdict == SyncVerdict.Synced).ToList();
        var notSyncing = launchers.Where(l => l.Verdict == SyncVerdict.NotSynced).ToList();
        if (syncing.Count > 0)
        {
            verdict = SyncVerdict.Synced;
            confidence = syncing.Max(l => l.Confidence);
        }
        else if (notSyncing.Count > 0)
        {
            verdict = SyncVerdict.NotSynced;
            confidence = notSyncing.Max(l => l.Confidence);
        }

        var strong = observed.LatestStrong;
        int clean = observed.CleanLaunches;
        bool changed = observed.HasChanges;

        if (strong is not null)
        {
            verdict = SyncVerdict.Synced;
            confidence = SyncConfidence.High;
            evidence.Add(StrongLine(strong));
        }
        else if (verdict == SyncVerdict.Unknown)
        {
            if (changed)
            {
                evidence.Add(ChangedLine(observed));
            }
            else if (clean >= CleanLaunchesForNotSynced)
            {
                verdict = SyncVerdict.NotSynced;
                confidence = clean >= CleanLaunchesForMedium ? SyncConfidence.Medium : SyncConfidence.Low;
            }
        }
        else if (changed)
        {
            evidence.Add(ChangedLine(observed));
            if (verdict == SyncVerdict.NotSynced && confidence > SyncConfidence.Low) confidence--; // something touched them anyway
        }

        foreach (var l in launchers) evidence.AddRange(l.Lines);
        if (clean > 0 && !changed)
            evidence.Add($"Settings were untouched between {clean} launch{(clean == 1 ? "" : "es")} on this device.");
        if (declared is bool d)
            evidence.Add(d ? "Research says this game's settings sync between machines."
                           : "Research says this game keeps its settings per machine.");
        else
            evidence.Add("Not researched yet: unknown (run cloud-check or the game-settings skill).");

        var report = new CloudSyncReport
        {
            GameId = gameId,
            Device = device,
            Declared = declared,
            Detected = verdict,
            Confidence = confidence,
            Evidence = evidence,
            HasExternalChanges = changed,
            CleanLaunches = clean,
            StrongChange = strong,
        };
        return report with { Contradiction = ContradictionFor(report), SuggestOptOut = report.Effective == false && !changed };
    }

    private static string? ContradictionFor(CloudSyncReport r)
    {
        if (r.Declared is null || r.Detected == SyncVerdict.Unknown || r.Confidence < SyncConfidence.Medium) return null;
        if (r.Declared == false && r.Detected == SyncVerdict.Synced)
        {
            return r.StrongChange is { FromDevice: { } from }
                ? $"Research says per-machine, but settings arrived from your {FriendlyDevice(from)}. Keep this game managed."
                : "Research says per-machine, but a launcher syncs the settings file. Keep this game managed.";
        }
        if (r.Declared == true && r.Detected == SyncVerdict.NotSynced)
            return "Research says the settings sync, but the launcher only syncs saves. They are probably per machine.";
        return null;
    }

    private static string StrongLine(ExternalChangeRecord c)
    {
        var preset = c.Preset is { Length: > 0 } p ? $"{c.FromDevice} {p}" : c.FromDevice;
        return $"Settings changed while closed and matched the {preset} preset ({c.Utc.ToLocalTime():MMM d}).";
    }

    private static string ChangedLine(ObservationSummary o)
    {
        var last = o.Changes.Max(c => c.Utc).ToLocalTime();
        return $"Settings changed {o.Changes.Count} time{(o.Changes.Count == 1 ? "" : "s")} while the game was closed (last {last:MMM d}), " +
               "but not to another device's preset.";
    }

    /// <summary>"ally-x" becomes "Ally X"; other device names are shown with dashes removed.</summary>
    public static string FriendlyDevice(string device) =>
        device.Equals("ally-x", StringComparison.OrdinalIgnoreCase) ? "Ally X" : device.Replace('-', ' ');
}
