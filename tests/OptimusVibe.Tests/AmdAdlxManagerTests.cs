using OptimusVibe.Config;
using OptimusVibe.Driver;
using Xunit;

namespace OptimusVibe.Tests;

public class AmdAdlxManagerTests
{
    internal sealed class FakeAdlx : IAdlxApi
    {
        public bool Available = true;
        public string Reason = "amdadlx64.dll not found; no AMD Adrenalin driver installed.";
        public readonly Dictionary<AmdSettingId, int> Values = new();
        public readonly HashSet<AmdSettingId> Unsupported = new();
        public readonly HashSet<AmdSettingId> FailWrites = new();
        public readonly Dictionary<AmdSettingId, (int Min, int Max)> Ranges = new();
        public readonly List<string> Writes = new();
        public int Sessions;
        public bool ChillRejectsCrossing;
        public readonly Dictionary<AmdSettingId, int> Steps = new();

        /// <summary>Writing a 1 to the key switches these off (mutually exclusive features).</summary>
        public readonly Dictionary<AmdSettingId, AmdSettingId[]> TurnsOff = new();

        /// <summary>Writes fail from this many successful writes on (a kill / driver reset mid-sequence).</summary>
        public int FailAfterWrites = int.MaxValue;

        public Action<AmdSettingId>? OnWrite;

        public AdlxInfo Info => new("C:\\Windows\\System32\\amdadlx64.dll", "2.0.0.1", ["AMD Radeon(TM) 890M (vendor 1002)"], "AMD Radeon(TM) 890M");

        public bool TryInitialize(out string reason)
        {
            reason = Available ? "" : Reason;
            return Available;
        }

        public IAdlxSession OpenSession()
        {
            Sessions++;
            return new Session(this);
        }

        private sealed class Session(FakeAdlx f) : IAdlxSession
        {
            public string GpuName => "AMD Radeon(TM) 890M";
            public string? LastError { get; private set; }

            public AdlxStatus Read(AmdSettingId id, out int value)
            {
                value = 0;
                if (f.Unsupported.Contains(id)) return AdlxStatus.Unsupported;
                if (id == AmdSettingId.FrtcFps) { value = f.Values.GetValueOrDefault(AmdSettingId.FrtcEnabled) != 0 ? f.Values.GetValueOrDefault(AmdSettingId.FrtcValue) : 0; return AdlxStatus.Ok; }
                if (id == AmdSettingId.AntiLag)
                {
                    value = f.Values.GetValueOrDefault(AmdSettingId.AntiLagEnabled) == 0 ? 0 : f.Values.GetValueOrDefault(AmdSettingId.AntiLagLevel) == 1 ? 2 : 1;
                    return AdlxStatus.Ok;
                }
                if (!f.Values.TryGetValue(id, out value)) { LastError = "no value"; return AdlxStatus.Error; }
                return AdlxStatus.Ok;
            }

            public AdlxStatus Write(AmdSettingId id, int value)
            {
                if (f.Unsupported.Contains(id)) return AdlxStatus.Unsupported;
                if (f.FailWrites.Contains(id)) { LastError = "ADLX_RESULT 3"; return AdlxStatus.Error; }
                if (f.ChillRejectsCrossing)
                {
                    var min = id == AmdSettingId.ChillMinFps ? value : f.Values[AmdSettingId.ChillMinFps];
                    var max = id == AmdSettingId.ChillMaxFps ? value : f.Values[AmdSettingId.ChillMaxFps];
                    if ((id is AmdSettingId.ChillMinFps or AmdSettingId.ChillMaxFps) && min > max) { LastError = "min > max"; return AdlxStatus.Error; }
                }
                if (f.Writes.Count >= f.FailAfterWrites) { LastError = "ADLX_RESULT 7"; return AdlxStatus.Error; }
                f.Writes.Add($"{id}={value}");
                if (id == AmdSettingId.FrtcFps)
                {
                    if (value == 0) f.Values[AmdSettingId.FrtcEnabled] = 0;
                    else
                    {
                        f.Values[AmdSettingId.FrtcValue] = value;
                        if (f.FailWrites.Contains(AmdSettingId.FrtcEnabled)) { LastError = "SetEnabled failed"; return AdlxStatus.Error; } // SetFPS ok, SetEnabled fails
                        f.Values[AmdSettingId.FrtcEnabled] = 1;
                    }
                }
                else if (id == AmdSettingId.AntiLag)
                {
                    if (value != 0) f.Values[AmdSettingId.AntiLagLevel] = value == 2 ? 1 : 0;
                    f.Values[AmdSettingId.AntiLagEnabled] = value != 0 ? 1 : 0;
                }
                else f.Values[id] = value;
                if (value != 0 && f.TurnsOff.TryGetValue(id == AmdSettingId.FrtcFps ? AmdSettingId.FrtcEnabled : id, out var off))
                    foreach (var o in off) f.Values[o] = 0;
                f.OnWrite?.Invoke(id);
                return AdlxStatus.Ok;
            }

            public bool TryGetRange(AmdSettingId id, out int min, out int max, out int step)
            {
                step = f.Steps.GetValueOrDefault(id, 1);
                if (f.Ranges.TryGetValue(id, out var r)) { (min, max) = r; return true; }
                min = max = 0;
                return false;
            }

            public void Dispose() { }
        }
    }

    private static GameDef Game() => new() { Name = "Test Game" };
    private static Dictionary<string, string> S(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    internal static FakeAdlx Ally() => new()
    {
        Values =
        {
            [AmdSettingId.FrtcEnabled] = 0,
            [AmdSettingId.FrtcValue] = 90,
            [AmdSettingId.ChillEnabled] = 0,
            [AmdSettingId.ChillMinFps] = 70,
            [AmdSettingId.ChillMaxFps] = 120,
            [AmdSettingId.AfmfEnabled] = 1,
            [AmdSettingId.RsrEnabled] = 0,
            [AmdSettingId.RsrSharpness] = 70,
            [AmdSettingId.AntiLagEnabled] = 1,
            [AmdSettingId.AntiLagLevel] = 0,
            [AmdSettingId.WaitForVSync] = 1,
        },
        Ranges =
        {
            [AmdSettingId.FrtcFps] = (15, 1000),
            [AmdSettingId.FrtcValue] = (15, 1000),
            [AmdSettingId.ChillMinFps] = (30, 300),
            [AmdSettingId.ChillMaxFps] = (30, 300),
            [AmdSettingId.RsrSharpness] = (0, 100),
            [AmdSettingId.RisSharpness] = (0, 100),
            [AmdSettingId.BoostMinResolution] = (10, 100),
        },
    };

    // ------------------------------------------------------------------ table

    [Theory]
    [InlineData("ChillEnabled", "true", 1)]
    [InlineData("ChillEnabled", "False", 0)]
    [InlineData("chillmaxfps", "45", 45)]
    [InlineData("FrtcFps", "off", 0)]
    [InlineData("FrtcFps", "60", 60)]
    [InlineData("AntiLag", "next", 2)]
    [InlineData("AntiLag", "true", 1)]
    [InlineData("WaitForVSync", "on", 3)]
    [InlineData("WaitForVSync", "app-off", 1)]
    [InlineData("RisSharpness", "100", 100)]
    public void Table_parses_valid_values(string key, string text, int expected)
    {
        Assert.True(AmdSettingTable.TryGet(key, out var s));
        Assert.Null(s.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("ChillEnabled", "maybe")]
    [InlineData("ChillMaxFps", "0")]
    [InlineData("ChillMaxFps", "fast")]
    [InlineData("RsrSharpness", "101")]
    [InlineData("AntiLag", "2x")]
    [InlineData("FrtcFps", "")]
    [InlineData("BoostMinResolution", "5")]
    public void Table_rejects_bad_values(string key, string text) =>
        Assert.NotNull(AmdSettingTable.Validate(key, text));

    [Fact]
    public void Table_accepts_the_short_aliases_from_the_config_templates()
    {
        Assert.Null(AmdSettingTable.Validate("AFMF", "false"));
        Assert.True(AmdSettingTable.TryGet("rsr", out var s));
        Assert.Equal(AmdSettingId.RsrEnabled, s.Id);
    }

    [Fact]
    public void Table_rejects_unknown_key_and_formats_values()
    {
        Assert.Contains("unknown amd setting", AmdSettingTable.Validate("Turbo", "1"));
        Assert.Null(AmdSettingTable.Validate("afmfenabled", "false"));
        Assert.Equal("off", AmdSettingTable.For(AmdSettingId.FrtcFps).Format(0));
        Assert.Equal("true", AmdSettingTable.For(AmdSettingId.AfmfEnabled).Format(1));
        Assert.Equal("app-on", AmdSettingTable.For(AmdSettingId.WaitForVSync).Format(2));
        Assert.Equal(AmdSettingTable.All.Count, AmdSettingTable.All.Select(s => s.Id).Distinct().Count());
        // every enum id is a table setting or one of the snapshot-only raw parts
        Assert.Equal(Enum.GetValues<AmdSettingId>().Length, AmdSettingTable.All.Count + 4);
        Assert.All(AmdSettingTable.SnapshotOrder, id => Assert.True(AmdSettingTable.SnapshotOrder.Count(x => x == id) == 1));
    }

    // ------------------------------------------------------------------ plan / fail soft

    [Fact]
    public void Unavailable_api_reports_reason_and_never_writes()
    {
        var api = new FakeAdlx { Available = false };
        var m = new AmdAdlxManager(api);
        Assert.False(m.IsAvailable(out var reason));
        Assert.Contains("amdadlx64.dll", reason);
        var plan = m.Plan(Game(), S(("ChillEnabled", "true")));
        Assert.Equal(new DriverChange("ChillEnabled", null, "true"), Assert.Single(plan));
        Assert.Null(m.Apply(Game(), S(("ChillEnabled", "true"))));
        Assert.False(m.Restore("{\"V\":1,\"Previous\":[{\"Key\":\"ChillEnabled\",\"Value\":0}]}")); // still changed, retry later
        Assert.Empty(api.Writes);
        Assert.Equal(0, api.Sessions);
    }

    [Fact]
    public void Plan_reads_current_values_without_writing()
    {
        var api = Ally();
        api.Unsupported.Add(AmdSettingId.RisEnabled);
        var plan = new AmdAdlxManager(api).Plan(Game(), S(("AfmfEnabled", "false"), ("ChillMaxFps", "45"), ("RisEnabled", "true"), ("Nope", "1")));
        Assert.Equal(3, plan.Count); // unknown key skipped
        Assert.Equal(new DriverChange("ChillMaxFps", "120", "45"), plan[0]);
        Assert.Equal(new DriverChange("AfmfEnabled", "true", "false"), plan[1]);
        Assert.Equal(new DriverChange("RisEnabled", "unsupported", "true"), plan[2]);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Invalid_values_and_unknown_keys_are_skipped_without_throwing()
    {
        var api = Ally();
        var token = new AmdAdlxManager(api).Apply(Game(), S(("ChillMaxFps", "fast"), ("Turbo", "1"), ("RsrSharpness", "500")));
        Assert.Null(token);
        Assert.Empty(api.Writes);
    }

    // ------------------------------------------------------------------ apply / restore

    [Fact]
    public void Apply_writes_deltas_and_returns_a_token_that_restore_undoes()
    {
        var api = Ally();
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("ChillEnabled", "true"), ("ChillMinFps", "30"), ("ChillMaxFps", "45"), ("AfmfEnabled", "false"), ("RsrEnabled", "false")));
        Assert.NotNull(token);
        Assert.Equal(1, api.Values[AmdSettingId.ChillEnabled]);
        Assert.Equal(30, api.Values[AmdSettingId.ChillMinFps]);
        Assert.Equal(45, api.Values[AmdSettingId.ChillMaxFps]);
        Assert.Equal(0, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.DoesNotContain(api.Writes, w => w.StartsWith("RsrEnabled")); // already false: not written, not in the token

        m.Restore(token!);
        Assert.Equal(0, api.Values[AmdSettingId.ChillEnabled]);
        Assert.Equal(70, api.Values[AmdSettingId.ChillMinFps]);
        Assert.Equal(120, api.Values[AmdSettingId.ChillMaxFps]);
        Assert.Equal(1, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.Equal(0, api.Values[AmdSettingId.RsrEnabled]);
    }

    [Fact]
    public void Apply_that_changes_nothing_returns_no_token()
    {
        var api = Ally();
        Assert.Null(new AmdAdlxManager(api).Apply(Game(), S(("AfmfEnabled", "true"), ("AntiLag", "on"))));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Chill_range_crossing_is_retried_so_min_above_old_max_still_applies_and_restores()
    {
        var api = Ally();
        api.ChillRejectsCrossing = true;
        api.Values[AmdSettingId.ChillMinFps] = 30;
        api.Values[AmdSettingId.ChillMaxFps] = 40;
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("ChillMinFps", "100"), ("ChillMaxFps", "140")));
        Assert.NotNull(token);
        Assert.Equal(100, api.Values[AmdSettingId.ChillMinFps]);
        Assert.Equal(140, api.Values[AmdSettingId.ChillMaxFps]);
        m.Restore(token!);
        Assert.Equal(30, api.Values[AmdSettingId.ChillMinFps]);
        Assert.Equal(40, api.Values[AmdSettingId.ChillMaxFps]);
    }

    [Fact]
    public void Unsupported_and_failing_settings_are_skipped_and_left_out_of_the_token()
    {
        var api = Ally();
        api.Unsupported.Add(AmdSettingId.AfmfEnabled);
        api.FailWrites.Add(AmdSettingId.RsrSharpness);
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("AfmfEnabled", "false"), ("RsrSharpness", "20"), ("AntiLag", "off")));
        Assert.NotNull(token);
        Assert.Equal(1, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.Equal(70, api.Values[AmdSettingId.RsrSharpness]);
        Assert.Equal(0, api.Values[AmdSettingId.AntiLagEnabled]);
        m.Restore(token!);
        Assert.Equal(1, api.Values[AmdSettingId.AntiLagEnabled]);
    }

    [Fact]
    public void Out_of_driver_range_value_is_skipped()
    {
        var api = Ally();
        api.Ranges[AmdSettingId.ChillMaxFps] = (30, 300);
        api.Ranges[AmdSettingId.FrtcFps] = (15, 1000);
        var token = new AmdAdlxManager(api).Apply(Game(), S(("ChillMaxFps", "20"), ("FrtcFps", "off")));
        Assert.Null(token); // 20 < 30 skipped; FrtcFps off == current 0
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Unreadable_current_value_is_not_written_because_it_could_not_be_restored()
    {
        var api = Ally();
        api.Values.Remove(AmdSettingId.WaitForVSync);
        Assert.Null(new AmdAdlxManager(api).Apply(Game(), S(("WaitForVSync", "off"))));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Restore_ignores_garbage_empty_and_unknown_tokens()
    {
        var api = Ally();
        var m = new AmdAdlxManager(api);
        m.Restore("");
        m.Restore("not json");
        m.Restore("{\"V\":1,\"Previous\":[{\"Key\":\"Nope\",\"Value\":1}]}");
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Status_lists_every_table_setting()
    {
        var api = Ally();
        api.Unsupported.Add(AmdSettingId.AfmfEnabled);
        var s = new AmdAdlxManager(api).Status();
        Assert.True(s.Available);
        Assert.Equal(AmdSettingTable.All.Count, s.Rows.Count);
        Assert.Equal("unsupported", s.Rows.First(r => r.Key == "AfmfEnabled").Value);
        Assert.StartsWith("error", s.Rows.First(r => r.Key == "RisSharpness").Value); // no value in the fake
    }

    // ------------------------------------------------------------------ diagnostics

    [Fact]
    public void Self_test_dry_run_does_not_write_and_apply_round_trips()
    {
        var api = Ally();
        api.Values[AmdSettingId.RisSharpness] = 80;
        api.Ranges[AmdSettingId.RisSharpness] = (10, 100);
        var m = new AmdAdlxManager(api);

        var dry = DriverDiagnostics.TestAmd(m, applyAndRestore: false, out var dryPassed);
        Assert.True(dryPassed);
        Assert.Empty(api.Writes);
        Assert.Contains(dry, l => l.Contains("RisSharpness"));

        var real = DriverDiagnostics.TestAmd(m, applyAndRestore: true, out var passed);
        Assert.True(passed, string.Join("\n", real));
        Assert.Equal(80, api.Values[AmdSettingId.RisSharpness]);
        Assert.Equal(["RisSharpness=81", "RisSharpness=80"], api.Writes);
    }

    [Fact]
    public void Self_test_moves_by_the_driver_step()
    {
        // The Ally's driver reports RIS sharpness 10..100 in steps of 10: +1 would be rounded straight back to 80.
        var api = Ally();
        api.Values[AmdSettingId.RisSharpness] = 80;
        api.Ranges[AmdSettingId.RisSharpness] = (10, 100);
        api.Steps[AmdSettingId.RisSharpness] = 10;
        var m = new AmdAdlxManager(api);

        Assert.Contains(DriverDiagnostics.AmdStatus(m), l => l.Contains("[10..100 step 10]"));
        var real = DriverDiagnostics.TestAmd(m, applyAndRestore: true, out var passed);
        Assert.True(passed, string.Join("\n", real));
        Assert.Equal(["RisSharpness=90", "RisSharpness=80"], api.Writes);
    }

    [Fact]
    public void Status_text_is_pasteable_and_reports_unavailable_driver()
    {
        var lines = DriverDiagnostics.AmdStatus(new AmdAdlxManager(new FakeAdlx { Available = false }));
        Assert.Contains("vendor: amd", lines);
        Assert.Contains(lines, l => l.StartsWith("adlx: unavailable"));
        Assert.Contains(lines, l => l.StartsWith("per-application profiles: not supported"));
    }

    // ------------------------------------------------------------------ review fixes: restore correctness

    [Fact]
    public void Failed_restore_reports_false_keeps_working_and_a_later_retry_succeeds()
    {
        var api = Ally();
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("AfmfEnabled", "false"), ("ChillMaxFps", "45")))!;
        api.FailWrites.Add(AmdSettingId.AfmfEnabled); // driver refuses the write-back
        Assert.False(m.Restore(token));
        Assert.Equal(0, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.Equal(120, api.Values[AmdSettingId.ChillMaxFps]); // the rest still came back
        api.FailWrites.Clear();
        Assert.True(m.Restore(token)); // same token, retried
        Assert.Equal(1, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.True(m.Restore(token)); // already equal: obsolete, nothing written
    }

    [Fact]
    public void Restore_while_the_driver_is_unavailable_keeps_the_token()
    {
        var api = Ally();
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("AfmfEnabled", "false")))!;
        api.Available = false;
        Assert.False(m.Restore(token));
        api.Available = true;
        Assert.True(m.Restore(token));
        Assert.Equal(1, api.Values[AmdSettingId.AfmfEnabled]);
    }

    [Fact]
    public void Full_snapshot_restores_a_feature_the_driver_turned_off_and_the_stored_numbers()
    {
        var api = Ally();
        api.Values[AmdSettingId.ChillEnabled] = 1;
        api.TurnsOff[AmdSettingId.FrtcEnabled] = [AmdSettingId.ChillEnabled]; // enabling FRTC switches Chill off
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("FrtcFps", "40")))!;
        Assert.Equal(0, api.Values[AmdSettingId.ChillEnabled]); // side effect we never asked for
        Assert.Equal(40, api.Values[AmdSettingId.FrtcValue]);

        Assert.True(m.Restore(token));
        Assert.Equal(1, api.Values[AmdSettingId.ChillEnabled]);
        Assert.Equal(0, api.Values[AmdSettingId.FrtcEnabled]);
        Assert.Equal(90, api.Values[AmdSettingId.FrtcValue]); // stored fps back, not just the enabled flag
    }

    [Fact]
    public void Restore_turns_conflicting_features_off_before_turning_others_on()
    {
        var api = Ally();
        api.Values[AmdSettingId.ChillEnabled] = 1;
        api.Values[AmdSettingId.FrtcEnabled] = 0;
        api.TurnsOff[AmdSettingId.FrtcEnabled] = [AmdSettingId.ChillEnabled];
        api.TurnsOff[AmdSettingId.ChillEnabled] = [AmdSettingId.FrtcEnabled];
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("FrtcFps", "40")))!;
        api.Writes.Clear();
        Assert.True(m.Restore(token));
        Assert.True(api.Writes.IndexOf("FrtcEnabled=0") < api.Writes.IndexOf("ChillEnabled=1"));
    }

    [Fact]
    public void Anti_lag_level_is_restored_separately_from_enabled()
    {
        var api = Ally();
        api.Values[AmdSettingId.AntiLagEnabled] = 0;
        api.Values[AmdSettingId.AntiLagLevel] = 1; // stored level "next" while off
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("AntiLag", "on")))!;
        Assert.Equal(0, api.Values[AmdSettingId.AntiLagLevel]);
        Assert.True(m.Restore(token));
        Assert.Equal(0, api.Values[AmdSettingId.AntiLagEnabled]);
        Assert.Equal(1, api.Values[AmdSettingId.AntiLagLevel]);
    }

    [Fact]
    public void Token_is_handed_out_before_the_first_write_so_a_kill_mid_apply_is_recoverable()
    {
        var api = Ally();
        var m = new AmdAdlxManager(api);
        string? saved = null;
        var writesAtSave = -1;
        api.FailAfterWrites = 1; // process killed / driver reset after the first write
        var returned = m.Apply(Game(), S(("AfmfEnabled", "false"), ("RsrEnabled", "true"), ("ChillMaxFps", "45")), t => { saved = t; writesAtSave = api.Writes.Count; });
        Assert.NotNull(saved);
        Assert.Equal(0, writesAtSave);
        Assert.Equal(saved, returned); // partially applied: the token is still returned
        api.FailAfterWrites = int.MaxValue;
        Assert.True(m.Restore(saved!)); // a new process recovers from the persisted token alone
        Assert.Equal(1, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.Equal(0, api.Values[AmdSettingId.RsrEnabled]);
        Assert.Equal(120, api.Values[AmdSettingId.ChillMaxFps]);
    }

    [Fact]
    public void Apply_that_throws_after_the_token_was_issued_still_returns_it()
    {
        var api = Ally();
        api.OnWrite = _ => throw new InvalidOperationException("boom");
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("AfmfEnabled", "false")), _ => { });
        Assert.NotNull(token);
    }

    [Fact]
    public void Partially_applied_frtc_write_is_still_tokenized_and_restorable()
    {
        var api = Ally();
        api.FailWrites.Add(AmdSettingId.FrtcEnabled); // SetFPS succeeds, SetEnabled fails
        var m = new AmdAdlxManager(api);
        var token = m.Apply(Game(), S(("FrtcFps", "40")));
        Assert.NotNull(token); // FrtcValue changed from 90 to 40 even though FRTC stayed off
        Assert.Equal(40, api.Values[AmdSettingId.FrtcValue]);
        api.FailWrites.Clear();
        Assert.True(m.Restore(token!));
        Assert.Equal(90, api.Values[AmdSettingId.FrtcValue]);
    }

    [Fact]
    public void V1_tokens_from_older_builds_still_restore()
    {
        var api = Ally();
        api.Values[AmdSettingId.ChillEnabled] = 1;
        var m = new AmdAdlxManager(api);
        Assert.True(m.Restore("{\"V\":1,\"Previous\":[{\"Key\":\"ChillEnabled\",\"Value\":0}]}"));
        Assert.Equal(0, api.Values[AmdSettingId.ChillEnabled]);
    }

    // ------------------------------------------------------------------ review fixes: ranges, steps, chill

    [Fact]
    public void Unknown_driver_range_skips_the_write_instead_of_writing_blind()
    {
        var api = Ally();
        api.Ranges.Remove(AmdSettingId.RsrSharpness);
        Assert.Null(new AmdAdlxManager(api).Apply(Game(), S(("RsrSharpness", "20"))));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Range_step_is_honoured_by_rounding()
    {
        var api = Ally();
        api.Ranges[AmdSettingId.RisSharpness] = (0, 100);
        api.Steps[AmdSettingId.RisSharpness] = 5;
        api.Values[AmdSettingId.RisSharpness] = 80;
        var token = new AmdAdlxManager(api).Apply(Game(), S(("RisSharpness", "52")));
        Assert.NotNull(token);
        Assert.Equal(50, api.Values[AmdSettingId.RisSharpness]);
    }

    [Fact]
    public void Chill_min_above_max_is_not_written()
    {
        var api = Ally();
        Assert.Null(new AmdAdlxManager(api).Apply(Game(), S(("ChillMinFps", "100"), ("ChillMaxFps", "50"))));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Alias_and_canonical_key_resolve_deterministically_to_the_canonical_one()
    {
        var api = Ally();
        var token = new AmdAdlxManager(api).Apply(Game(), S(("AFMF", "true"), ("AfmfEnabled", "false")));
        Assert.NotNull(token);
        Assert.Equal(0, api.Values[AmdSettingId.AfmfEnabled]);
        Assert.Equal(["AfmfEnabled=0"], api.Writes);
    }

    [Fact]
    public void Block_validation_flags_duplicates_and_chill_crossing()
    {
        Assert.Contains(AmdSettingTable.ValidateBlock(S(("AFMF", "true"), ("AfmfEnabled", "false"))), e => e.Contains("same setting"));
        Assert.Contains(AmdSettingTable.ValidateBlock(S(("ChillMinFps", "100"), ("ChillMaxFps", "50"))), e => e.Contains("ChillMinFps"));
        Assert.Empty(AmdSettingTable.ValidateBlock(S(("ChillMinFps", "30"), ("ChillMaxFps", "50"), ("AFMF", "true"))));
    }

    // ------------------------------------------------------------------ review fixes: native layer

    [Fact]
    public void Init_failure_is_not_cached_and_a_later_success_is()
    {
        var calls = 0;
        var native = new AdlxNative((out string reason) =>
        {
            calls++;
            reason = calls < 3 ? "driver not ready" : "";
            return calls >= 3;
        });
        Assert.False(native.TryInitialize(out var r1));
        Assert.Equal("driver not ready", r1);
        Assert.False(native.TryInitialize(out _));
        Assert.True(native.TryInitialize(out _));
        Assert.True(native.TryInitialize(out _));
        Assert.Equal(3, calls); // success cached, failures were retried
    }

    [Theory]
    [InlineData(new[] { 1, 2 }, 1)]    // integrated + discrete AMD: discrete wins
    [InlineData(new[] { 2, 1 }, 0)]
    [InlineData(new[] { 1, 1 }, 0)]    // no discrete: first AMD
    [InlineData(new[] { 0 }, 0)]
    public void Discrete_amd_gpu_is_preferred(int[] types, int expected)
    {
        var gpus = types.Select(t => ((string?)"1002", t)).ToList();
        Assert.Equal(expected, AdlxNative.ChooseGpu(gpus));
    }

    [Fact]
    public void Gpu_choice_ignores_non_amd_and_reports_none()
    {
        Assert.Equal(1, AdlxNative.ChooseGpu([("10DE", 2), ("1002", 1)]));
        Assert.Equal(-1, AdlxNative.ChooseGpu([("10DE", 2)]));
    }

    // ------------------------------------------------------------------ NVIDIA values (Profile Inspector CustomSettingNames.xml)

    [Fact]
    public void Nvidia_values_match_profile_inspector()
    {
        var rr = NvidiaSettingTable.Values["DlssRrPreset"];
        foreach (var (name, v) in new[] { ("G", 0x7u), ("H", 0x8u), ("I", 0x9u), ("O", 0xFu) })
            Assert.Contains((name, v), rr);
        Assert.Contains(("NotApplicable", 0x3u), NvidiaSettingTable.Values["DlssQualityLevel"]);
        Assert.Contains(("GB5", 0x1400u), NvidiaSettingTable.Values["ShaderCacheSize"]);
    }

    // ------------------------------------------------------------------ NVIDIA table additions

    [Theory]
    [InlineData("DlssPreset", 0x10E41DF3u)]
    [InlineData("DlssOverrideDll", 0x10E41E01u)]
    [InlineData("UltraLowLatency", 0x10835000u)]
    [InlineData("ShaderCacheSize", 0x00AC8497u)]
    [InlineData("SmoothMotion", 0xB0D384C0u)]
    public void Nvidia_table_has_the_profile_inspector_settings(string name, uint id)
    {
        Assert.Equal(id, NvidiaSettingTable.Ids[name]);
        Assert.True(NvidiaSettingTable.Values.ContainsKey(name));
    }
}
