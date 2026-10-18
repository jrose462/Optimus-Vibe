using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimusVibe.GameLab;

/// <summary>What a request asks the lab to do.</summary>
public enum LabKind
{
    /// <summary>The game has no config yet: research and create it.</summary>
    New,

    /// <summary>The game has a config: re-research it (patched game, better evidence).</summary>
    Refresh,

    /// <summary>Only re-check whether the game's settings travel through cloud sync.</summary>
    SyncCheck,
}

/// <summary>Where a request is in its life, derived from the issue's lab:* labels.</summary>
public enum LabStatus { Unknown, Queued, Researching, NeedsEvidence, Review, Done, Failed }

/// <summary>
/// The wire contract shared with the workflow in the user's config repo (seed\config-repo): labels, hidden markers,
/// the request issue and the comment formats. Pure string work, no I/O.
/// </summary>
public static class GameLabContract
{
    public const string WorkflowFile = "game-lab.yml";
    public const string SecretName = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>The gate's daily-limit note: "&lt;!-- optimus-vibe:lab-queued v1 yyyy-MM-dd --&gt;" (UTC day), once per day.</summary>
    public const string QueuedMarkerPrefix = "<!-- optimus-vibe:lab-queued v1";
    public const string DailyMaxVariable = "GAME_LAB_DAILY_MAX";
    public const string ModelVariable = "GAME_LAB_MODEL";
    public const int DefaultDailyMax = 5;
    public const string DefaultModel = "claude-sonnet-5-5";

    public const string LabelGameLab = "game-lab";
    public const string LabelQueued = "lab:queued";
    public const string LabelResearching = "lab:researching";
    public const string LabelNeedsEvidence = "lab:needs-evidence";
    public const string LabelReview = "lab:review";
    public const string LabelDone = "lab:done";
    public const string LabelFailed = "lab:failed";

    public static readonly IReadOnlyList<(string Name, string Color, string Description)> Labels =
    [
        (LabelGameLab, "1f6feb", "Optimus-Vibe game lab request"),
        (LabelQueued, "d4c5f9", "Waiting for the lab to pick it up"),
        (LabelResearching, "fbca04", "Claude is researching this game"),
        (LabelNeedsEvidence, "d93f0b", "The lab needs more files from your PC"),
        (LabelReview, "0e8a16", "Presets written, pull request open"),
        (LabelDone, "0e8a16", "Presets merged"),
        (LabelFailed, "b60205", "The lab could not finish this request"),
    ];

    public const string RequestMarker = "<!-- optimus-vibe:game-lab v1 -->";
    public const string EvidenceMarker = "<!-- optimus-vibe:lab-evidence v1 -->";
    public const string NeedsEvidenceMarker = "<!-- optimus-vibe:lab-needs-evidence v1 -->";
    public const string ResultMarker = "<!-- optimus-vibe:lab-result v1 -->";

    /// <summary>GitHub's limit is 65,536 characters; evidence parts stay well below it.</summary>
    public const int MaxPartChars = 60_000;

    // ------------------------------------------------------------ kinds

    public static string KindText(LabKind kind) => kind switch
    {
        LabKind.New => "new",
        LabKind.Refresh => "refresh",
        _ => "sync-check",
    };

    public static bool TryParseKind(string? text, out LabKind kind)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "new": kind = LabKind.New; return true;
            case "refresh": kind = LabKind.Refresh; return true;
            case "sync-check" or "synccheck" or "sync_check" or "sync": kind = LabKind.SyncCheck; return true;
            default: kind = LabKind.New; return false;
        }
    }

    // ------------------------------------------------------------ request issue

    /// <summary>The JSON in the <c>lab-request</c> comment.</summary>
    public sealed record RequestInfo(LabKind Kind, string GameId, string Name, string Store, string StoreId, string Device, string AppVersion)
    {
        /// <summary>"steam:1234" — what dedupe compares.</summary>
        public string Key => $"{Store}:{StoreId}";
    }

    public static string Title(string name, LabKind kind) =>
        $"[game-lab] {Regex.Replace(name ?? "", @"\s+", " ").Trim()} - {KindText(kind)}";

    public static string RequestJson(RequestInfo r) =>
        new JsonObject
        {
            ["kind"] = KindText(r.Kind),
            ["gameId"] = r.GameId ?? "",
            ["name"] = r.Name ?? "",
            ["store"] = r.Store ?? "",
            ["storeId"] = r.StoreId ?? "",
            ["device"] = r.Device ?? "",
            ["appVersion"] = r.AppVersion ?? "",
        }.ToJsonString(); // the default encoder escapes < > & so the JSON can never close the HTML comment

    /// <summary>
    /// The issue body up to (and excluding) the first evidence block: marker, <c>lab-request</c> comment, human text.
    /// <paramref name="firstEvidence"/> is appended after it when given.
    /// </summary>
    public static string RequestBody(RequestInfo r, string? firstEvidence = null)
    {
        var sb = new StringBuilder();
        sb.Append(RequestMarker).Append('\n');
        sb.Append("<!-- lab-request ").Append(RequestJson(r)).Append(" -->\n\n");
        sb.Append(HumanText(r)).Append("\n\n");
        if (!string.IsNullOrEmpty(firstEvidence)) sb.Append(firstEvidence);
        return sb.ToString();
    }

    private static string HumanText(RequestInfo r)
    {
        var what = r.Kind switch
        {
            LabKind.New => "Research and write recommended graphics presets for this game.",
            LabKind.Refresh => "Refresh this game's presets (patched game or better evidence).",
            _ => "Re-check whether this game's settings travel through cloud sync (presets stay as they are).",
        };
        var sb = new StringBuilder();
        sb.Append("**Game:** ").Append(Cell(r.Name)).Append(" (`").Append(r.Store).Append(':').Append(r.StoreId).Append("`)\n");
        sb.Append("**Request:** ").Append(KindText(r.Kind)).Append(" - ").Append(what).Append('\n');
        if (!string.IsNullOrWhiteSpace(r.GameId)) sb.Append("**Game id:** `").Append(r.GameId).Append("`\n");
        sb.Append("**Device:** ").Append(Cell(r.Device)).Append("  \n");
        sb.Append("**App:** Optimus-Vibe ").Append(Cell(r.AppVersion)).Append('\n');
        sb.Append("\nFiled by the Optimus-Vibe app. The evidence below was collected from the owner's PC and had personal data redacted.");
        return sb.ToString();
    }

    private static string Cell(string? s) => Regex.Replace(s ?? "", @"[\r\n`]+", " ").Trim();

    /// <summary>Parses the marker + <c>lab-request</c> comment of an issue body; null when this is not a lab request.</summary>
    public static RequestInfo? ParseRequest(string? body)
    {
        if (string.IsNullOrEmpty(body) || !body.Contains(RequestMarker, StringComparison.Ordinal)) return null;
        var m = Regex.Match(body, @"<!--\s*lab-request\s+(\{.*?\})\s*-->", RegexOptions.Singleline);
        if (!m.Success) return null;
        try
        {
            var j = JsonNode.Parse(m.Groups[1].Value);
            if (j is null || !TryParseKind(j["kind"]?.GetValue<string>(), out var kind)) return null;
            string S(string key) => j[key]?.GetValue<string>() ?? "";
            return new RequestInfo(kind, S("gameId"), S("name"), S("store"), S("storeId"), S("device"), S("appVersion"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------ labels -> status

    /// <summary>Most advanced state wins; an issue with only "game-lab" is Unknown.</summary>
    public static LabStatus StatusFromLabels(IEnumerable<string> labels)
    {
        var set = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Contains(LabelFailed)) return LabStatus.Failed;
        if (set.Contains(LabelDone)) return LabStatus.Done;
        if (set.Contains(LabelNeedsEvidence)) return LabStatus.NeedsEvidence;
        if (set.Contains(LabelReview)) return LabStatus.Review;
        if (set.Contains(LabelResearching)) return LabStatus.Researching;
        if (set.Contains(LabelQueued)) return LabStatus.Queued;
        return LabStatus.Unknown;
    }

    public static string StatusText(LabStatus s) => s switch
    {
        LabStatus.Queued => "queued",
        LabStatus.Researching => "researching",
        LabStatus.NeedsEvidence => "needs evidence",
        LabStatus.Review => "in review",
        LabStatus.Done => "done",
        LabStatus.Failed => "failed",
        _ => "unknown",
    };

    // ------------------------------------------------------------ comments from the workflow

    /// <summary>First ```json block of a comment, parsed; null when absent or invalid.</summary>
    internal static JsonNode? FirstJsonBlock(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"```json[^\S\r\n]*\r?\n(.*?)\r?\n[^\S\r\n]*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try { return JsonNode.Parse(m.Groups[1].Value); }
        catch (JsonException) { return null; }
    }

    /// <summary>The ```json block of a needs-evidence comment; null when this is not one or the JSON is unusable.</summary>
    public static NeedsEvidence? ParseNeedsEvidence(string? comment)
    {
        if (comment is null || !comment.TrimStart().StartsWith(NeedsEvidenceMarker, StringComparison.Ordinal)) return null;
        if (FirstJsonBlock(comment) is not JsonObject j) return null;
        static List<string> Strings(JsonNode? n) =>
            n is JsonArray a ? a.Select(x => { try { return x?.GetValue<string>(); } catch (InvalidOperationException) { return null; } })
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList() : [];
        return new NeedsEvidence(Strings(j["files"]), Strings(j["registry"]), Strings(j["listDirs"]));
    }

    /// <summary>Parses a result comment (<c>lab-result</c>); null when it isn't one.</summary>
    public static LabResult? ParseResult(string? comment)
    {
        if (comment is null || !comment.TrimStart().StartsWith(ResultMarker, StringComparison.Ordinal)) return null;
        if (FirstJsonBlock(comment) is not JsonObject j) return null;
        var presets = new List<string>();
        int? presetCount = null;
        switch (j["presets"])
        {
            case JsonArray arr:
                presets.AddRange(arr.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0));
                presetCount = presets.Count;
                break;
            case JsonObject obj:
                presets.AddRange(obj.Select(kv => kv.Key));
                presetCount = presets.Count;
                break;
            case JsonValue v when v.TryGetValue<int>(out var n):
                presetCount = n;
                break;
        }
        bool? Bool(string key) { try { return j[key]?.GetValue<bool>(); } catch (InvalidOperationException) { return null; } }
        int? Int(string key) { try { return j[key]?.GetValue<int>(); } catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; } }
        return new LabResult(
            j["gameId"]?.ToString() ?? "",
            presets,
            presetCount ?? 0,
            Bool("cloudSyncsSettings"),
            j["syncConfidence"]?.ToString(),
            Int("prNumber"),
            Bool("merged") ?? false);
    }

    /// <summary>The 'part i/n' line + marker that start an evidence comment.</summary>
    public static string EvidenceHeader(int part, int parts) =>
        EvidenceMarker + "\n" + (parts > 1 ? $"_Evidence part {part}/{parts}_\n\n" : "\n");
}

/// <summary>What the workflow asked the app to collect (the JSON of a needs-evidence comment).</summary>
public sealed record NeedsEvidence(IReadOnlyList<string> Files, IReadOnlyList<string> Registry, IReadOnlyList<string> ListDirs)
{
    public bool IsEmpty => Files.Count == 0 && Registry.Count == 0 && ListDirs.Count == 0;
}

/// <summary>The JSON of a result comment.</summary>
/// <param name="Presets">Preset names when the workflow listed them (array of names or an object keyed by name).</param>
/// <param name="PresetCount">How many presets were written (the array/object size, or the number given).</param>
public sealed record LabResult(string GameId, IReadOnlyList<string> Presets, int PresetCount, bool? CloudSyncsSettings,
    string? SyncConfidence, int? PrNumber, bool Merged);
