using System.Text.RegularExpressions;
using OptimusVibe.Config;

namespace OptimusVibe.Launch;

/// <summary>Pure rules for how a game is launched: waiting time, apply timing, merged arguments, the Steam URI form.</summary>
public static class LaunchPolicy
{
    public const int DefaultWaitSeconds = 180;
    public const int LauncherWaitSeconds = 600;
    public const int DefaultSettleSeconds = 5;

    /// <summary>Stores whose launchers show prompts (EULA, family picker, updates) so the game can start minutes late.</summary>
    private static readonly string[] PromptyStores = ["ea", "battlenet", "epic"];

    public static readonly string[] ApplyTimings = ["before", "afterProcessStart", "both"];

    public static readonly string[] Archetypes =
    [
        "plain-honoured", "binary-truth", "text-mirror", "composite", "local-cloud-split",
        "first-run", "registry", "launcher-gated", "server-held",
    ];

    /// <summary>launch.waitSeconds, else 600 for the stores with prompting launchers (or a game that lists launcher processes), else 180.</summary>
    public static TimeSpan StartupTimeout(GameDef game)
    {
        if (game.Launch?.WaitSeconds is > 0 and var s) return TimeSpan.FromSeconds(s);
        var prompty = PromptyStores.Contains(game.Store, StringComparer.OrdinalIgnoreCase);
        return TimeSpan.FromSeconds(prompty ? LauncherWaitSeconds : DefaultWaitSeconds);
    }

    /// <summary>The effective apply timing: the game's applyTiming, else "both" for games whose cloud rewrites settings, else "before".</summary>
    public static string EffectiveTiming(GameDef game)
    {
        var t = ApplyTimings.FirstOrDefault(a => a.Equals(game.ApplyTiming?.Trim(), StringComparison.OrdinalIgnoreCase));
        return t ?? (game.CloudSyncsSettings == true ? "both" : "before");
    }

    public static bool AppliesBefore(GameDef game) => EffectiveTiming(game) != "afterProcessStart";
    public static bool AppliesAfterStart(GameDef game) => EffectiveTiming(game) != "before";

    public static TimeSpan Settle(GameDef game) =>
        TimeSpan.FromSeconds(game.SettleSeconds is >= 0 and var s ? s : DefaultSettleSeconds);

    // ---------------------------------------------------------------- arguments

    private static readonly Regex NumberLike = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsFlag(string token) => token.Length > 1 && (token[0] is '-' or '+') && !NumberLike.IsMatch(token);

    private static string FlagKey(string token)
    {
        var eq = token.IndexOf('=');
        return (eq > 0 ? token[..eq] : token).ToLowerInvariant();
    }

    /// <summary>Splits tokens into flag groups: "+com_x 120" and "--fullscreen" are one group each; a bare leading value is its own group.</summary>
    private static List<(string Key, List<string> Tokens)> Groups(IEnumerable<string> tokens)
    {
        var groups = new List<(string, List<string>)>();
        foreach (var t in tokens.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (IsFlag(t) || groups.Count == 0)
                groups.Add((IsFlag(t) ? FlagKey(t) : t, [t]));
            else
                groups[^1].Item2.Add(t);
        }
        return groups;
    }

    /// <summary>The game's launch.args with the preset's launch.args on top: the same flag appears once, the preset's value winning.</summary>
    public static List<string> MergeArgs(IEnumerable<string>? gameArgs, IEnumerable<string>? presetArgs)
    {
        var preset = Groups(presetArgs ?? []);
        var presetKeys = preset.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Groups(gameArgs ?? []).Where(g => !presetKeys.Contains(g.Key)))
            if (seen.Add(g.Key)) result.AddRange(g.Tokens);
        foreach (var g in preset)
            if (seen.Add(g.Key)) result.AddRange(g.Tokens);
        return result;
    }

    /// <summary>Append extra args to a command line, skipping a flag the command already carries (so a user's own Steam option wins).</summary>
    public static List<string> AppendArgs(IReadOnlyList<string> command, IReadOnlyList<string> extra)
    {
        var result = command.ToList();
        if (extra.Count == 0) return result;
        var present = command.Skip(1).Where(IsFlag).Select(FlagKey).ToHashSet(StringComparer.Ordinal);
        foreach (var g in Groups(extra))
            if (!present.Contains(g.Key)) result.AddRange(g.Tokens);
        return result;
    }

    /// <summary>steam://rungameid/&lt;id&gt; with the arguments in Steam's `//&lt;url-encoded args&gt;` form.</summary>
    public static string SteamUri(int appId, IReadOnlyList<string> args)
    {
        var uri = $"steam://rungameid/{appId}";
        return args.Count == 0 ? uri : $"{uri}//{Uri.EscapeDataString(string.Join(' ', args.Select(Quote)))}";
    }

    /// <summary>Adds args to a steam://rungameid/&lt;id&gt; URI that has none; any other URI is returned unchanged.</summary>
    public static string WithArgs(string uri, IReadOnlyList<string> args)
    {
        const string prefix = "steam://rungameid/";
        if (args.Count == 0 || !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || uri.IndexOf("//", prefix.Length, StringComparison.Ordinal) >= 0) return uri;
        return int.TryParse(uri[prefix.Length..].TrimEnd('/'), out var id) ? SteamUri(id, args) : uri;
    }

    private static string Quote(string token) => token.Any(char.IsWhiteSpace) ? $"\"{token}\"" : token;

    // ---------------------------------------------------------------- status words

    /// <summary>
    /// One honest line for a game's preset from its archetype and the last verify run on this device: what applying does and whether it held.
    /// </summary>
    public static string Describe(GameDef game, ResolvedProfile profile, RunResult? lastVerify)
    {
        if (game.Mappable == false) return "In-game steps only: the game's settings can't be set from outside, so this preset is advice.";
        if (profile.FileBlocks.Count == 0 && profile.SnapshotsFrom is null) return "No settings are written for this preset.";

        var how = game.Archetype?.Trim().ToLowerInvariant() switch
        {
            "binary-truth" => "Needs a saved snapshot - cloud sync can overwrite the text file",
            "text-mirror" => "Applied to a text mirror the game may not read back",
            "launcher-gated" => "Applied by file edit once the launcher hands over",
            "server-held" => "Only the local graphics file is applied",
            "first-run" => "Applied by file edit after the game has run once",
            "registry" => "Applied in the registry",
            "local-cloud-split" => "Applied across a local and a cloud-synced file",
            _ => profile.SnapshotsFrom is not null ? "Applied from a saved snapshot" : "Applied by file edit",
        };
        if (lastVerify is null) return $"{how} - unverified on this device.";
        return RunResults.Verdict(lastVerify) switch
        {
            RunVerdict.Held => $"{how} - verified held on {lastVerify.Utc.ToLocalTime():yyyy-MM-dd}.",
            RunVerdict.Overridden => $"{how} - the game overrode {lastVerify.Drift.Count} setting(s) when last tested.",
            _ => $"{how} - unverified on this device.",
        };
    }
}
