using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Driver;

/// <summary>
/// AMD (ROG Xbox Ally X, Adrenalin driver): reads and writes the GLOBAL 3D graphics settings through ADLX (<see cref="AdlxNative"/>).
/// ADLX has no per-application scope, so <see cref="Apply"/> records every value it changes in a JSON restore token and the runner
/// calls <see cref="Restore"/> after the game exits (same contract as the display-mode restore). Per-setting problems
/// (unknown key, bad value, unsupported by this driver, native error) are logged and skipped; nothing here throws into the launch.
/// Keys are listed in <see cref="AmdSettingTable"/> and documented in docs/amd-adlx.md.
/// </summary>
public sealed class AmdAdlxManager : IDriverSettings
{
    private readonly IAdlxApi _api;

    public AmdAdlxManager() : this(AdlxNative.Instance)
    {
    }

    internal AmdAdlxManager(IAdlxApi api) => _api = api;

    public string Vendor => "amd";

    public bool IsAvailable(out string reason) => _api.TryInitialize(out reason);

    private sealed record Resolved(AmdSetting Setting, int Target);

    private sealed class Token
    {
        public int V { get; set; } = 1;
        public string? Gpu { get; set; }

        /// <summary>Previous values in the order they were changed (setting key -> integer value).</summary>
        public List<KeyValuePair<string, int>> Previous { get; set; } = new();
    }

    // ---------------------------------------------------------------------------------------------

    public IReadOnlyList<DriverChange> Plan(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        var resolved = Resolve(settings);
        if (resolved.Count == 0) return Array.Empty<DriverChange>();

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"AMD: driver settings unavailable ({reason}); current values unknown.");
            return resolved.Select(r => new DriverChange(r.Setting.Key, null, r.Setting.Format(r.Target))).ToList();
        }

        try
        {
            using var session = _api.OpenSession();
            return resolved.Select(r =>
            {
                var st = session.Read(r.Setting.Id, out var cur);
                var current = st switch
                {
                    AdlxStatus.Ok => r.Setting.Format(cur),
                    AdlxStatus.Unsupported => "unsupported",
                    _ => null,
                };
                return new DriverChange(r.Setting.Key, current, r.Setting.Format(r.Target));
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"AMD: could not read the driver settings for {game.Name}: {ex.Message}");
            return resolved.Select(r => new DriverChange(r.Setting.Key, null, r.Setting.Format(r.Target))).ToList();
        }
    }

    public string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        var resolved = Resolve(settings);
        if (resolved.Count == 0) return null;

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"AMD: driver settings skipped for {game.Name}: {reason}");
            return null;
        }

        try
        {
            using var session = _api.OpenSession();
            var pending = new List<(Resolved R, int Previous)>();
            foreach (var r in resolved)
            {
                var st = session.Read(r.Setting.Id, out var cur);
                if (st == AdlxStatus.Unsupported)
                {
                    Log.Warn($"AMD: {r.Setting.Key} is not supported by this driver/GPU; skipped.");
                    continue;
                }
                if (st == AdlxStatus.Error)
                {
                    Log.Warn($"AMD: could not read {r.Setting.Key} ({session.LastError}); skipped so it can be restored safely.");
                    continue;
                }
                if (cur == r.Target)
                {
                    Log.Info($"AMD: {r.Setting.Key} already {r.Setting.Format(r.Target)}.");
                    continue;
                }
                if (r.Setting.Kind == AmdKind.Int && !(r.Setting.OffMeansZero && r.Target == 0) &&
                    session.TryGetRange(r.Setting.Id, out var min, out var max) && (r.Target < min || r.Target > max))
                {
                    Log.Warn($"AMD: {r.Setting.Key} = {r.Target} is outside the driver's range {min}..{max}; skipped.");
                    continue;
                }
                pending.Add((r, cur));
            }

            var applied = WriteAll(session, pending.Select(p => (p.R.Setting, p.R.Target)).ToList());
            var token = new Token { Gpu = session.GpuName };
            foreach (var (r, previous) in pending)
            {
                if (!applied.Contains(r.Setting.Id)) continue;
                token.Previous.Add(new(r.Setting.Key, previous));
                Log.Info($"AMD: {r.Setting.Key} = {r.Setting.Format(r.Target)} (was {r.Setting.Format(previous)}; global, restored after the game exits)");
                VerifyReadBack(session, r);
            }
            return token.Previous.Count == 0 ? null : JsonSerializer.Serialize(token);
        }
        catch (Exception ex)
        {
            Log.Warn($"AMD: applying driver settings for {game.Name} failed: {ex.Message}");
            return null;
        }
    }

    public void Restore(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        Token? t;
        try { t = JsonSerializer.Deserialize<Token>(token); }
        catch (Exception ex)
        {
            Log.Warn($"AMD: restore token unreadable ({ex.Message}); nothing restored.");
            return;
        }
        if (t is null || t.Previous.Count == 0) return;

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"AMD: restore skipped: {reason}");
            return;
        }

        try
        {
            using var session = _api.OpenSession();
            var writes = new List<(AmdSetting, int)>();
            // Undo in reverse order of application.
            foreach (var (key, value) in Enumerable.Reverse(t.Previous))
            {
                if (!AmdSettingTable.TryGet(key, out var setting))
                {
                    Log.Warn($"AMD: restore token has unknown setting '{key}'; skipped.");
                    continue;
                }
                writes.Add((setting, value));
            }
            var done = WriteAll(session, writes);
            foreach (var (setting, value) in writes)
                if (done.Contains(setting.Id)) Log.Info($"AMD: restored {setting.Key} = {setting.Format(value)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"AMD: restore failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes every pair; failures are retried once after the others (Chill min/max reject a value that crosses the other bound until it moves).
    /// Returns the ids that were written successfully. Unsupported or failing settings are logged.
    /// </summary>
    private static HashSet<AmdSettingId> WriteAll(IAdlxSession session, IReadOnlyList<(AmdSetting Setting, int Value)> writes)
    {
        var ok = new HashSet<AmdSettingId>();
        var retry = new List<(AmdSetting Setting, int Value)>();
        foreach (var w in writes)
        {
            var st = session.Write(w.Setting.Id, w.Value);
            if (st == AdlxStatus.Ok) ok.Add(w.Setting.Id);
            else if (st == AdlxStatus.Error) retry.Add(w);
            else Log.Warn($"AMD: {w.Setting.Key} is not supported by this driver/GPU; skipped.");
        }
        foreach (var w in retry)
        {
            var st = session.Write(w.Setting.Id, w.Value);
            if (st == AdlxStatus.Ok) ok.Add(w.Setting.Id);
            else Log.Warn($"AMD: could not write {w.Setting.Key} = {w.Setting.Format(w.Value)}: {session.LastError ?? "unsupported"}");
        }
        return ok;
    }

    private static void VerifyReadBack(IAdlxSession session, Resolved r)
    {
        if (session.Read(r.Setting.Id, out var now) == AdlxStatus.Ok && now != r.Target)
            Log.Warn($"AMD: wrote {r.Setting.Key} = {r.Setting.Format(r.Target)} but the driver now reports {r.Setting.Format(now)}.");
    }

    private static List<Resolved> Resolve(IReadOnlyDictionary<string, string> settings)
    {
        var map = new Dictionary<AmdSettingId, Resolved>();
        foreach (var (rawKey, rawValue) in settings)
        {
            var key = (rawKey ?? "").Trim();
            if (!AmdSettingTable.TryGet(key, out var setting))
            {
                Log.Warn($"AMD: unknown driver setting '{key}' (known: {string.Join(", ", AmdSettingTable.All.Select(a => a.Key))}); skipped.");
                continue;
            }
            var error = setting.TryParse(rawValue, out var target);
            if (error is not null)
            {
                Log.Warn($"AMD: invalid value for {setting.Key}: {error}; skipped.");
                continue;
            }
            map[setting.Id] = new Resolved(setting, target);
        }
        // Table order = apply order.
        return AmdSettingTable.All.Where(s => map.ContainsKey(s.Id)).Select(s => map[s.Id]).ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Diagnostics (driver status / test-amd)

    /// <summary>One row of `driver status`: a table setting with its current value or why there is none.</summary>
    internal sealed record StatusRow(string Key, string Value, string Range);

    internal sealed record StatusReport(bool Available, string Reason, AdlxInfo? Info, IReadOnlyList<StatusRow> Rows);

    internal StatusReport Status()
    {
        if (!IsAvailable(out var reason)) return new StatusReport(false, reason, null, []);
        var rows = new List<StatusRow>();
        try
        {
            using var session = _api.OpenSession();
            foreach (var s in AmdSettingTable.All)
            {
                var st = session.Read(s.Id, out var v);
                var value = st switch
                {
                    AdlxStatus.Ok => s.Format(v),
                    AdlxStatus.Unsupported => "unsupported",
                    _ => $"error: {session.LastError}",
                };
                var range = s.Kind == AmdKind.Int && session.TryGetRange(s.Id, out var min, out var max) ? $"{min}..{max}" : "";
                rows.Add(new StatusRow(s.Key, value, range));
            }
        }
        catch (Exception ex)
        {
            return new StatusReport(true, $"session failed: {ex.Message}", _api.Info, rows);
        }
        return new StatusReport(true, "", _api.Info, rows);
    }
}
