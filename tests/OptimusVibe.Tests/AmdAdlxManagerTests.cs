using OptimusVibe.Config;
using OptimusVibe.Driver;
using Xunit;

namespace OptimusVibe.Tests;

public class AmdAdlxManagerTests
{
    private sealed class FakeAdlx : IAdlxApi
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
                f.Writes.Add($"{id}={value}");
                f.Values[id] = value;
                return AdlxStatus.Ok;
            }

            public bool TryGetRange(AmdSettingId id, out int min, out int max)
            {
                if (f.Ranges.TryGetValue(id, out var r)) { (min, max) = r; return true; }
                min = max = 0;
                return false;
            }

            public void Dispose() { }
        }
    }

    private static GameDef Game() => new() { Name = "Test Game" };
    private static Dictionary<string, string> S(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    private static FakeAdlx Ally() => new()
    {
        Values =
        {
            [AmdSettingId.FrtcFps] = 0,
            [AmdSettingId.ChillEnabled] = 0,
            [AmdSettingId.ChillMinFps] = 70,
            [AmdSettingId.ChillMaxFps] = 120,
            [AmdSettingId.AfmfEnabled] = 1,
            [AmdSettingId.RsrEnabled] = 0,
            [AmdSettingId.RsrSharpness] = 70,
            [AmdSettingId.AntiLag] = 1,
            [AmdSettingId.WaitForVSync] = 1,
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
        Assert.Equal(Enum.GetValues<AmdSettingId>().Length, AmdSettingTable.All.Count);
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
        m.Restore("{\"V\":1,\"Previous\":[{\"Key\":\"ChillEnabled\",\"Value\":0}]}");
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
        Assert.Equal(0, api.Values[AmdSettingId.AntiLag]);
        m.Restore(token!);
        Assert.Equal(1, api.Values[AmdSettingId.AntiLag]);
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
    public void Status_text_is_pasteable_and_reports_unavailable_driver()
    {
        var lines = DriverDiagnostics.AmdStatus(new AmdAdlxManager(new FakeAdlx { Available = false }));
        Assert.Contains("vendor: amd", lines);
        Assert.Contains(lines, l => l.StartsWith("adlx: unavailable"));
        Assert.Contains(lines, l => l.StartsWith("per-application profiles: not supported"));
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
