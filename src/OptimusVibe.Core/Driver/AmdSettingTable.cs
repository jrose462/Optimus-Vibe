using System.Collections.Frozen;
using System.Globalization;

namespace OptimusVibe.Driver;

internal enum AmdKind { Bool, Int, Enum }

/// <summary>
/// One friendly `amd:` key. <c>Min</c>/<c>Max</c> are static sanity limits for <see cref="AmdKind.Int"/> (the driver's own range is checked again
/// at apply time). <c>OffMeansZero</c> lets an Int setting take "off" (FrtcFps).
/// </summary>
internal sealed record AmdSetting(string Key, AmdSettingId Id, AmdKind Kind, int Min, int Max, string Help,
    (string Name, int Value)[]? Names = null, bool OffMeansZero = false)
{
    /// <summary>Parses a YAML scalar into the integer form used by <see cref="IAdlxSession"/>. Null error = ok.</summary>
    public string? TryParse(string? raw, out int value)
    {
        value = 0;
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return $"empty value (expected {Help})";
        switch (Kind)
        {
            case AmdKind.Bool:
                if (TryBool(text, out var b)) { value = b ? 1 : 0; return null; }
                return $"'{text}' is not a boolean (expected {Help})";
            case AmdKind.Enum:
                foreach (var (name, v) in Names!)
                    if (name.Equals(text, StringComparison.OrdinalIgnoreCase)) { value = v; return null; }
                if (TryBool(text, out var eb) && BoolEnum(eb, out value)) return null;
                value = 0;
                return $"'{text}' is not one of {string.Join(" | ", Names.Select(n => n.Name))}";
            default:
                if (OffMeansZero && (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)))
                {
                    value = 0;
                    return null;
                }
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return $"'{text}' is not a whole number (expected {Help})";
                if (value < Min || value > Max) return $"{value} is out of range {Min}..{Max} (expected {Help})";
                return null;
        }
    }

    public string Format(int value) => Kind switch
    {
        AmdKind.Bool => value != 0 ? "true" : "false",
        AmdKind.Enum => Names!.FirstOrDefault(n => n.Value == value).Name ?? value.ToString(CultureInfo.InvariantCulture),
        _ => OffMeansZero && value == 0 ? "off" : value.ToString(CultureInfo.InvariantCulture),
    };

    // true/false for an enum maps to its "on" / "off" entries (AntiLag, WaitForVSync).
    private bool BoolEnum(bool on, out int value)
    {
        var wanted = on ? "on" : "off";
        foreach (var (name, v) in Names!)
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = 0;
        return false;
    }

    internal static bool TryBool(string text, out bool b)
    {
        switch (text.ToLowerInvariant())
        {
            case "true" or "on" or "yes" or "1": b = true; return true;
            case "false" or "off" or "no" or "0": b = false; return true;
            default: b = false; return false;
        }
    }
}

/// <summary>
/// Friendly keys for `amd:` blocks in game presets (device driver: amd). ADLX exposes these as GLOBAL 3D graphics settings,
/// so they are applied before launch and restored after exit. Key names are Optimus-Vibe's own vocabulary; the ADLX interfaces
/// behind them are listed in docs/amd-adlx.md.
/// </summary>
internal static class AmdSettingTable
{
    private static readonly (string, int)[] AntiLagNames = [("off", 0), ("on", 1), ("next", 2)];
    private static readonly (string, int)[] VSyncNames = [("off", 0), ("app-off", 1), ("app-on", 2), ("on", 3)];

    /// <summary>Order is the apply order: numbers first, enable flags last, so a half-applied set never switches a feature on with stale numbers.</summary>
    internal static readonly IReadOnlyList<AmdSetting> All =
    [
        new("FrtcFps", AmdSettingId.FrtcFps, AmdKind.Int, 1, 1000, "fps (Frame Rate Target Control) or off", OffMeansZero: true),
        new("ChillMinFps", AmdSettingId.ChillMinFps, AmdKind.Int, 1, 1000, "fps (Radeon Chill minimum)"),
        new("ChillMaxFps", AmdSettingId.ChillMaxFps, AmdKind.Int, 1, 1000, "fps (Radeon Chill maximum)"),
        new("ChillEnabled", AmdSettingId.ChillEnabled, AmdKind.Bool, 0, 1, "true | false (Radeon Chill)"),
        new("RsrSharpness", AmdSettingId.RsrSharpness, AmdKind.Int, 0, 100, "0-100 (Radeon Super Resolution sharpness)"),
        new("RsrEnabled", AmdSettingId.RsrEnabled, AmdKind.Bool, 0, 1, "true | false (Radeon Super Resolution)"),
        new("AfmfEnabled", AmdSettingId.AfmfEnabled, AmdKind.Bool, 0, 1, "true | false (AMD Fluid Motion Frames)"),
        new("AntiLag", AmdSettingId.AntiLag, AmdKind.Enum, 0, 2, "off | on | next (Anti-Lag / Anti-Lag 2)", AntiLagNames),
        new("RisSharpness", AmdSettingId.RisSharpness, AmdKind.Int, 0, 100, "0-100 (Radeon Image Sharpening)"),
        new("RisEnabled", AmdSettingId.RisEnabled, AmdKind.Bool, 0, 1, "true | false (Radeon Image Sharpening)"),
        new("WaitForVSync", AmdSettingId.WaitForVSync, AmdKind.Enum, 0, 3, "off | app-off | app-on | on (Wait for Vertical Refresh)", VSyncNames),
        new("EnhancedSync", AmdSettingId.EnhancedSync, AmdKind.Bool, 0, 1, "true | false (Enhanced Sync)"),
        new("BoostMinResolution", AmdSettingId.BoostMinResolution, AmdKind.Int, 10, 100, "10-100 (Radeon Boost minimum resolution %)"),
        new("BoostEnabled", AmdSettingId.BoostEnabled, AmdKind.Bool, 0, 1, "true | false (Radeon Boost)"),
    ];

    private static readonly FrozenDictionary<string, AmdSetting> ByKey =
        All.ToFrozenDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    // Short names the config templates used before the backend existed (`amd: { AFMF: false }`).
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AFMF"] = "AfmfEnabled",
        ["RSR"] = "RsrEnabled",
        ["RIS"] = "RisEnabled",
        ["Chill"] = "ChillEnabled",
        ["FRTC"] = "FrtcFps",
        ["Boost"] = "BoostEnabled",
        ["VSync"] = "WaitForVSync",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGet(string key, out AmdSetting setting)
    {
        var k = (key ?? "").Trim();
        if (Aliases.TryGetValue(k, out var canonical)) k = canonical;
        return ByKey.TryGetValue(k, out setting!);
    }

    internal static bool IsAlias(string key) => Aliases.ContainsKey((key ?? "").Trim());

    /// <summary>Settings captured in the restore token: every readable setting, with the composite FrtcFps / AntiLag split into their raw parts. Numbers first, then enable flags.</summary>
    internal static readonly IReadOnlyList<AmdSettingId> SnapshotOrder =
    [
        AmdSettingId.FrtcValue, AmdSettingId.ChillMinFps, AmdSettingId.ChillMaxFps, AmdSettingId.RsrSharpness, AmdSettingId.RisSharpness,
        AmdSettingId.BoostMinResolution, AmdSettingId.AntiLagLevel, AmdSettingId.WaitForVSync,
        AmdSettingId.FrtcEnabled, AmdSettingId.ChillEnabled, AmdSettingId.RsrEnabled, AmdSettingId.AfmfEnabled, AmdSettingId.AntiLagEnabled,
        AmdSettingId.RisEnabled, AmdSettingId.EnhancedSync, AmdSettingId.BoostEnabled,
    ];

    private static readonly HashSet<AmdSettingId> Flags =
    [
        AmdSettingId.FrtcEnabled, AmdSettingId.ChillEnabled, AmdSettingId.RsrEnabled, AmdSettingId.AfmfEnabled, AmdSettingId.AntiLagEnabled,
        AmdSettingId.RisEnabled, AmdSettingId.EnhancedSync, AmdSettingId.BoostEnabled,
    ];

    /// <summary>True for on/off switches (restored after the numbers, off before on, so a conflicting feature is released first).</summary>
    internal static bool IsFlag(AmdSettingId id) => Flags.Contains(id);

    internal sealed record Entry(string Key, string Value, AmdSetting Setting);

    /// <summary>
    /// Known keys of an `amd:` block with duplicates removed: an alias and its canonical key (AFMF + AfmfEnabled) name the same setting, so the
    /// canonical key wins (then the ordinal-first key); the losers are returned in <paramref name="dropped"/>. Unknown keys are left out.
    /// </summary>
    internal static List<Entry> Dedupe(IReadOnlyDictionary<string, string> block, out List<(string Dropped, string Kept)> dropped)
    {
        dropped = [];
        var won = new Dictionary<AmdSettingId, Entry>();
        foreach (var kv in block.OrderBy(k => IsAlias(k.Key) ? 1 : 0).ThenBy(k => k.Key.Trim(), StringComparer.Ordinal))
        {
            if (!TryGet(kv.Key, out var s)) continue;
            if (won.TryGetValue(s.Id, out var first)) { dropped.Add((kv.Key.Trim(), first.Key)); continue; }
            won[s.Id] = new Entry(kv.Key.Trim(), kv.Value, s);
        }
        return won.Values.ToList();
    }

    /// <summary>Whole-block errors for `validate` (per-key errors come from <see cref="Validate"/>): alias + canonical key together, Chill min above max.</summary>
    public static List<string> ValidateBlock(IReadOnlyDictionary<string, string> block, bool duplicates = true)
    {
        var errors = new List<string>();
        var entries = Dedupe(block, out var dropped);
        if (duplicates) foreach (var (d, kept) in dropped) errors.Add($"amd.{d} and amd.{kept} set the same setting; keep one");
        int? min = null, max = null;
        foreach (var e in entries)
        {
            if (e.Setting.TryParse(e.Value, out var v) is not null) continue;
            if (e.Setting.Id == AmdSettingId.ChillMinFps) min = v;
            if (e.Setting.Id == AmdSettingId.ChillMaxFps) max = v;
        }
        if (min > max) errors.Add($"amd.ChillMinFps ({min}) is above amd.ChillMaxFps ({max})");
        return errors;
    }

    internal static AmdSetting For(AmdSettingId id) => All.First(s => s.Id == id);

    /// <summary>Null when the key and value are valid; otherwise a message for `validate`.</summary>
    public static string? Validate(string key, string? value)
    {
        if (!TryGet(key, out var s))
            return $"unknown amd setting '{key}' (known: {string.Join(", ", All.Select(a => a.Key))})";
        return s.TryParse(value, out _);
    }
}
