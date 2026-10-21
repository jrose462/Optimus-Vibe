using System.Globalization;
using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Driver;

/// <summary>
/// AMD (ROG Xbox Ally X, Adrenalin driver): reads and writes the GLOBAL 3D graphics settings through ADLX (<see cref="AdlxNative"/>).
/// ADLX has no per-application scope, so <see cref="Apply"/> snapshots every readable setting into a JSON restore token before it writes
/// and the runner calls <see cref="Restore"/> after the game exits (same contract as the display-mode restore). Restore writes back every
/// setting that differs from the snapshot and reports whether everything is back; the caller keeps the token until it is. Per-setting
/// problems (unknown key, bad value, unsupported by this driver, native error) are logged and skipped; nothing here throws into the launch.
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
        public int V { get; set; } = 2;
        public string? Gpu { get; set; }

        /// <summary>v2: every readable setting before anything was written (<see cref="AmdSettingId"/> name -> raw integer), including the raw FRTC / Anti-Lag parts.</summary>
        public Dictionary<string, int> Snapshot { get; set; } = new();

        /// <summary>v1 (older builds): previous values of just the changed settings, in the order they were changed.</summary>
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

    public string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings, Action<string>? beforeWrite = null)
    {
        var resolved = Resolve(settings);
        if (resolved.Count == 0) return null;

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"AMD: driver settings skipped for {game.Name}: {reason}");
            return null;
        }

        string? issued = null;
        try
        {
            using var session = _api.OpenSession();
            var snapshot = TakeSnapshot(session);
            var pending = new List<(Resolved R, int Previous)>();
            foreach (var r0 in resolved)
            {
                var r = r0;
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
                if (r.Setting.Kind == AmdKind.Int && !(r.Setting.OffMeansZero && r.Target == 0))
                {
                    if (!session.TryGetRange(r.Setting.Id, out var min, out var max, out var step))
                    {
                        Log.Warn($"AMD: the driver did not report a range for {r.Setting.Key}; skipped rather than writing blind.");
                        continue;
                    }
                    if (r.Target < min || r.Target > max)
                    {
                        Log.Warn($"AMD: {r.Setting.Key} = {r.Target} is outside the driver's range {min}..{max}; skipped.");
                        continue;
                    }
                    if (step > 1 && (r.Target - min) % step != 0)
                    {
                        var rounded = Math.Min(max, min + (int)Math.Round((double)(r.Target - min) / step) * step);
                        Log.Warn($"AMD: {r.Setting.Key} = {r.Target} is not a multiple of the driver's step {step}; using {rounded}.");
                        r = r with { Target = rounded };
                        if (cur == r.Target) continue;
                    }
                }
                pending.Add((r, cur));
            }

            var minPending = pending.FirstOrDefault(p => p.R.Setting.Id == AmdSettingId.ChillMinFps);
            var maxPending = pending.FirstOrDefault(p => p.R.Setting.Id == AmdSettingId.ChillMaxFps);
            if (minPending.R is not null && maxPending.R is not null && minPending.R.Target > maxPending.R.Target)
            {
                Log.Warn($"AMD: ChillMinFps {minPending.R.Target} is above ChillMaxFps {maxPending.R.Target}; both skipped.");
                pending.RemoveAll(p => p.R.Setting.Id is AmdSettingId.ChillMinFps or AmdSettingId.ChillMaxFps);
            }
            if (pending.Count == 0) return null;

            // The full snapshot is the restore token. Hand it to the caller to persist BEFORE the first write, so a kill mid-apply is recoverable.
            issued = SerializeSnapshot(session.GpuName, snapshot);
            beforeWrite?.Invoke(issued);

            var applied = WriteAll(session, pending.Select(p => (p.R.Setting.Id, p.R.Setting.Key, p.R.Setting.Format(p.R.Target), p.R.Target)).ToList());
            foreach (var (r, previous) in pending)
            {
                if (!applied.Contains(r.Setting.Id)) continue;
                Log.Info($"AMD: {r.Setting.Key} = {r.Setting.Format(r.Target)} (was {r.Setting.Format(previous)}; global, restored after the game exits)");
                VerifyReadBack(session, r);
            }

            // A token is only needed if something really differs from the snapshot now (this also catches a half-applied multi-call write
            // and side effects such as the driver switching a mutually exclusive feature off).
            return Differing(session, snapshot).Count == 0 ? null : issued;
        }
        catch (Exception ex)
        {
            Log.Warn($"AMD: applying driver settings for {game.Name} failed: {ex.Message}");
            return issued; // writes may have happened: keep the token so the caller restores
        }
    }

    public bool Restore(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        Token? t;
        try { t = JsonSerializer.Deserialize<Token>(token); }
        catch (Exception ex)
        {
            Log.Warn($"AMD: restore token unreadable ({ex.Message}); it can never be restored and is dropped.");
            return true;
        }
        if (t is null) return true;

        var targets = new List<(AmdSettingId Id, int Value)>();
        if (t.Snapshot.Count > 0)
        {
            foreach (var id in AmdSettingTable.SnapshotOrder)
                if (t.Snapshot.TryGetValue(id.ToString(), out var v)) targets.Add((id, v));
            foreach (var key in t.Snapshot.Keys.Where(k => !AmdSettingTable.SnapshotOrder.Any(i => i.ToString() == k)))
                Log.Warn($"AMD: restore token has unknown setting '{key}'; skipped.");
        }
        else
        {
            // v1 token: undo in reverse order of application.
            foreach (var (key, value) in Enumerable.Reverse(t.Previous))
            {
                if (!AmdSettingTable.TryGet(key, out var setting))
                {
                    Log.Warn($"AMD: restore token has unknown setting '{key}'; skipped.");
                    continue;
                }
                targets.Add((setting.Id, value));
            }
        }
        if (targets.Count == 0) return true;

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"AMD: restore postponed, the settings are still changed: {reason}");
            return false;
        }

        try
        {
            using var session = _api.OpenSession();
            var wanted = new Dictionary<AmdSettingId, int>();
            foreach (var (id, v) in targets) wanted[id] = v;
            var order = targets.Select(x => x.Id).ToList();

            // A few passes: restoring one feature can make the driver flip another (mutually exclusive features), which the next pass fixes.
            for (var pass = 0; pass < 3; pass++)
            {
                var diff = Differing(session, order, wanted);
                if (diff.Count == 0) return true;
                // numbers first, then switches that turn a feature OFF, then switches that turn one ON
                var ordered = diff.Where(x => !AmdSettingTable.IsFlag(x.Id))
                    .Concat(diff.Where(x => AmdSettingTable.IsFlag(x.Id) && x.Value == 0))
                    .Concat(diff.Where(x => AmdSettingTable.IsFlag(x.Id) && x.Value != 0))
                    .Select(x => (x.Id, Label(x.Id), Fmt(x.Id, x.Value), x.Value)).ToList();
                var done = WriteAll(session, ordered);
                foreach (var o in ordered)
                    if (done.Contains(o.Id)) Log.Info($"AMD: restored {o.Item2} = {o.Item3}");
            }
            var left = Differing(session, order, wanted);
            if (left.Count == 0) return true;
            Log.Warn($"AMD: could not restore {string.Join(", ", left.Select(x => $"{Label(x.Id)} (want {Fmt(x.Id, x.Value)})"))}; the token is kept and restore is retried on the next start.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"AMD: restore failed: {ex.Message}; the token is kept and restore is retried on the next start.");
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Reads every snapshot setting the driver exposes (unsupported / unreadable ones are left out: they cannot have been changed by us).</summary>
    private static Dictionary<AmdSettingId, int> TakeSnapshot(IAdlxSession session)
    {
        var snap = new Dictionary<AmdSettingId, int>();
        foreach (var id in AmdSettingTable.SnapshotOrder)
            if (session.Read(id, out var v) == AdlxStatus.Ok) snap[id] = v;
        return snap;
    }

    private static string SerializeSnapshot(string? gpu, Dictionary<AmdSettingId, int> snapshot) =>
        JsonSerializer.Serialize(new Token { Gpu = gpu, Snapshot = snapshot.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) });

    private static List<(AmdSettingId Id, int Value)> Differing(IAdlxSession session, IReadOnlyDictionary<AmdSettingId, int> wanted) =>
        Differing(session, AmdSettingTable.SnapshotOrder.Where(wanted.ContainsKey).ToList(), wanted);

    /// <summary>The settings whose current value differs from the wanted one (or cannot be read, so a write may not have held).</summary>
    private static List<(AmdSettingId Id, int Value)> Differing(IAdlxSession session, IReadOnlyList<AmdSettingId> order, IReadOnlyDictionary<AmdSettingId, int> wanted)
    {
        var left = new List<(AmdSettingId, int)>();
        foreach (var id in order)
        {
            var want = wanted[id];
            var st = session.Read(id, out var cur);
            if (st == AdlxStatus.Unsupported) continue;
            if (st == AdlxStatus.Ok && cur == want) continue;
            left.Add((id, want));
        }
        return left;
    }

    private static string Label(AmdSettingId id) => AmdSettingTable.All.FirstOrDefault(s => s.Id == id)?.Key ?? id.ToString();

    private static string Fmt(AmdSettingId id, int v) =>
        AmdSettingTable.All.FirstOrDefault(s => s.Id == id)?.Format(v) ?? v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes every pair; failures are retried once after the others (Chill min/max reject a value that crosses the other bound until it moves).
    /// Returns the ids that were written successfully. Unsupported or failing settings are logged.
    /// </summary>
    private static HashSet<AmdSettingId> WriteAll(IAdlxSession session, IReadOnlyList<(AmdSettingId Id, string Key, string Text, int Value)> writes)
    {
        var ok = new HashSet<AmdSettingId>();
        var retry = new List<(AmdSettingId Id, string Key, string Text, int Value)>();
        foreach (var w in writes)
        {
            var st = session.Write(w.Id, w.Value);
            if (st == AdlxStatus.Ok) ok.Add(w.Id);
            else if (st == AdlxStatus.Error) retry.Add(w);
            else Log.Warn($"AMD: {w.Key} is not supported by this driver/GPU; skipped.");
        }
        foreach (var w in retry)
        {
            var st = session.Write(w.Id, w.Value);
            if (st == AdlxStatus.Ok) ok.Add(w.Id);
            else if (st == AdlxStatus.Error && WriteWhileSwitchedOn(session, w.Id, w.Key, w.Value)) ok.Add(w.Id);
            else Log.Warn($"AMD: could not write {w.Key} = {w.Text}: {session.LastError ?? "unsupported"}");
        }
        return ok;
    }

    /// <summary>
    /// A number whose feature is off may be rejected (the Ally's RIS sharpness while RIS is off). Switches the feature on, writes the number and
    /// switches it back off, so only the number changes. Returns false (switch left as found where possible) if that doesn't work either.
    /// </summary>
    private static bool WriteWhileSwitchedOn(IAdlxSession session, AmdSettingId id, string key, int value)
    {
        if (AmdSettingTable.SwitchFor(id) is not { } sw) return false;
        if (session.Read(sw, out var on) != AdlxStatus.Ok || on != 0) return false;
        if (session.Write(sw, 1) != AdlxStatus.Ok) return false;
        var st = session.Write(id, value);
        var off = session.Write(sw, 0);
        if (off != AdlxStatus.Ok) Log.Warn($"AMD: switched {Label(sw)} on to change {key} but could not switch it back off; it is restored after the game exits.");
        if (st == AdlxStatus.Ok) Log.Info($"AMD: {key} can only be changed while {Label(sw)} is on; switched it on for the change and back off.");
        return st == AdlxStatus.Ok;
    }

    private static void VerifyReadBack(IAdlxSession session, Resolved r)
    {
        if (session.Read(r.Setting.Id, out var now) == AdlxStatus.Ok && now != r.Target)
            Log.Warn($"AMD: wrote {r.Setting.Key} = {r.Setting.Format(r.Target)} but the driver now reports {r.Setting.Format(now)}.");
    }

    private static List<Resolved> Resolve(IReadOnlyDictionary<string, string> settings)
    {
        foreach (var rawKey in settings.Keys)
            if (!AmdSettingTable.TryGet((rawKey ?? "").Trim(), out _))
                Log.Warn($"AMD: unknown driver setting '{(rawKey ?? "").Trim()}' (known: {string.Join(", ", AmdSettingTable.All.Select(a => a.Key))}); skipped.");

        var map = new Dictionary<AmdSettingId, Resolved>();
        var entries = AmdSettingTable.Dedupe(settings, out var dropped);
        foreach (var e in entries)
        {
            var error = e.Setting.TryParse(e.Value, out var target);
            if (error is not null)
            {
                Log.Warn($"AMD: invalid value for {e.Setting.Key}: {error}; skipped.");
                continue;
            }
            map[e.Setting.Id] = new Resolved(e.Setting, target);
        }
        foreach (var (d, kept) in dropped) Log.Warn($"AMD: '{d}' and '{kept}' set the same setting; using '{kept}'.");
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
                var range = s.Kind == AmdKind.Int && session.TryGetRange(s.Id, out var min, out var max, out var step) ? (step > 1 ? $"{min}..{max} step {step}" : $"{min}..{max}") : "";
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
