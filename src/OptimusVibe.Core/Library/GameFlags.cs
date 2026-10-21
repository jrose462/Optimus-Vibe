using System.Globalization;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Library;

/// <summary>
/// "Set in-game": a game that ignores the settings Optimus-Vibe writes gets "settings off, rest on" (no preset, saved-setting or snapshot writes,
/// no re-apply, no drift prompt, no Test preset; display, graphics-driver settings, launch args and the launch watchdog stay). The flag has two sources and
/// either one counts: the game file's <c>flagged:</c> (the game lab / the skill decided) and library.yaml's <c>setInGame</c> (the user clicked
/// "Flag as set in-game"; synced between devices like opt-outs). One place answers "is this game flagged, and why".
/// </summary>
public static class GameFlags
{
    /// <summary>Two Test preset results in a row where the game overrode the preset make the flag worth suggesting.</summary>
    public const int SuggestAfterOverridden = 2;

    /// <summary>The reason shown when a flag has none (a hand-written <c>flagged:</c> without one is a validation error, but it must still work).</summary>
    public const string FallbackReason = "the game ignores the settings Optimus-Vibe writes";

    /// <summary>The flag that counts for a game: the game file's, else the user's in library.yaml. Null = managed normally.</summary>
    public static FlagDef? Effective(GameDef game, LibraryFile? library) => FromConfig(game) ?? FromLibrary(library, game.Id);

    /// <summary><see cref="Effective(GameDef, LibraryFile?)"/> reading library.yaml from the store; an unreadable file counts as no user flag.</summary>
    public static FlagDef? Effective(GameDef game, ConfigStore store)
    {
        try { return Effective(game, LibraryService.LoadLibrary(store)); }
        catch (ConfigException ex)
        {
            Log.Warn($"Could not read the library flags: {ex.Message}");
            return FromConfig(game);
        }
    }

    /// <summary>The game file's <c>flagged:</c>, or null.</summary>
    public static FlagDef? FromConfig(GameDef game) => game.Flagged;

    /// <summary>The user's flag for a game in library.yaml (case-insensitive game id), or null.</summary>
    public static FlagDef? FromLibrary(LibraryFile? library, string gameId)
    {
        if (library?.SetInGame is not { Count: > 0 } map || string.IsNullOrWhiteSpace(gameId)) return null;
        foreach (var (key, flag) in map)
            if (flag is not null && key.Trim().Equals(gameId.Trim(), StringComparison.OrdinalIgnoreCase)) return flag;
        return null;
    }

    /// <summary>The reason to show: the flag's, or <see cref="FallbackReason"/>.</summary>
    public static string ReasonText(FlagDef flag) =>
        string.IsNullOrWhiteSpace(flag.Reason) ? FallbackReason : flag.Reason.Trim().TrimEnd('.');

    /// <summary>A new user flag stamped with today's date (UTC unless <paramref name="today"/> is given).</summary>
    public static FlagDef Create(string reason, int? issue = null, string? evidence = null, DateTime? today = null) => new()
    {
        Reason = reason.Trim(),
        Since = (today ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Evidence = string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim(),
        Issue = issue,
    };

    /// <summary>The one line a run prints for a flagged game.</summary>
    public static string RunLine(FlagDef flag) => $"Settings left to the game (set in-game: {ReasonText(flag)})";

    /// <summary>"Set in-game: why. Optimus-Vibe still sets the display and graphics-driver settings." plus "Research: #12." when there is an issue.</summary>
    public static string StatusLine(FlagDef flag) =>
        $"Set in-game: {ReasonText(flag)}. Optimus-Vibe still sets the display and graphics-driver settings." +
        (flag.Issue is { } n ? $" Research: #{n}." : "");

    /// <summary>The reason a Test preset result suggests: "the game overrode the preset in 2 tests in a row".</summary>
    public static string VerifySuggestionReason(int streak) => $"the game overrode the preset in {streak} tests in a row";

    /// <summary>The reason a mapping run suggests: "the settings only changed in a binary file (system.save) that Optimus-Vibe can't edit".</summary>
    public static string MappingSuggestionReason(IReadOnlyList<string> opaqueFiles)
    {
        var names = opaqueFiles.Select(p => Path.GetFileName(p.Replace('\\', '/'))).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var which = names.Count == 0 ? "a binary file" : names.Count == 1 ? $"a binary file ({names[0]})" : $"binary files ({string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", ..." : "")})";
        return $"the game keeps its settings only in {which} that Optimus-Vibe can't edit";
    }

    /// <summary>Whether a streak of overridden Test preset results is long enough to suggest the flag.</summary>
    public static bool SuggestsFromVerify(int overriddenStreak) => overriddenStreak >= SuggestAfterOverridden;

    /// <summary>The reason normal launches suggest: "the game changed the preset's settings after your last 2 launches".</summary>
    public static string PassiveSuggestionReason(int streak) => $"the game changed the preset's settings after your last {streak} launches";

    /// <summary>Whether a streak of passively overridden normal runs (<see cref="Launch.RunResultEntry.PassiveOverriddenStreak"/>) is long enough to suggest the flag.</summary>
    public static bool SuggestsFromPassive(int passiveOverriddenStreak) => passiveOverriddenStreak >= SuggestAfterOverridden;
}
