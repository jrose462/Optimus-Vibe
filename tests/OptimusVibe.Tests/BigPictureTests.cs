using OptimusVibe.Config;
using OptimusVibe.Display;
using OptimusVibe.Engine;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

public sealed class BigPictureTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    private static DeviceDef Desktop(BigPictureDef? bp = null) => new()
    {
        Display = new DisplayModeDef { Width = 5120, Height = 2160, Refresh = 165 },
        BigPicture = bp,
    };

    private static ActiveDisplay Ultrawide(bool primary = true) => new(@"\.\DISPLAY1", "LG ULTRAGEAR+", 5120, 2160, 165, primary);
    private static ActiveDisplay Tv(bool primary = false) => new(@"\.\DISPLAY2", "LG TV SSCR2", 3840, 2160, 120, primary);

    private static Dictionary<string, string> Env(string w, string h, string fps = "120.000") =>
        new() { ["SUNSHINE_CLIENT_WIDTH"] = w, ["SUNSHINE_CLIENT_HEIGHT"] = h, ["SUNSHINE_CLIENT_FPS"] = fps, ["SUNSHINE_CLIENT_HDR"] = "true" };

    [Fact]
    public void UltrawideOnly_IsInactive() =>
        Assert.False(BigPictureDetector.Detect(Desktop(), [Ultrawide()], NoEnv).Active);

    [Fact]
    public void UltrawidePlus4kTv_ActiveTargetingTv()
    {
        var r = BigPictureDetector.Detect(Desktop(), [Ultrawide(), Tv()], NoEnv);
        Assert.True(r.Active);
        Assert.Equal(@"\.\DISPLAY2", r.TargetDisplay);
        Assert.Equal("TV display 'LG TV SSCR2' 3840x2160", r.Reason);
        Assert.Equal("big-picture", r.Preset);
    }

    [Fact]
    public void AllyPanelPlusDocked4kTv_Active()
    {
        var ally = new DeviceDef { Display = new DisplayModeDef { Width = 1920, Height = 1080, Refresh = 120 } };
        var panel = new ActiveDisplay(@"\.\DISPLAY1", "Built-in", 1920, 1080, 120, true);
        Assert.True(BigPictureDetector.Detect(ally, [panel, Tv()], NoEnv).Active);
        Assert.False(BigPictureDetector.Detect(ally, [panel], NoEnv).Active);
    }

    [Fact]
    public void NameRule_MatchesEvenBelowMinWidth()
    {
        var dev = Desktop(new BigPictureDef { Match = new BigPictureMatchDef { Name = new List<object> { "Sony*", "LG TV*" }, MinWidth = 5000 } });
        var r = BigPictureDetector.Detect(dev, [Ultrawide(), Tv()], NoEnv);
        Assert.True(r.Active);
        Assert.Equal(@"\.\DISPLAY2", r.TargetDisplay);
    }

    [Fact]
    public void Stream4k_ActiveWithoutTarget()
    {
        var r = BigPictureDetector.Detect(Desktop(), [Ultrawide()], Env("3840", "2160"));
        Assert.True(r.Active);
        Assert.Null(r.TargetDisplay);
        Assert.Equal("stream client 3840x2160@120", r.Reason);
    }

    [Fact]
    public void ApolloVariables_AreSupported() =>
        Assert.True(BigPictureDetector.Detect(Desktop(), [], new Dictionary<string, string> { ["APOLLO_CLIENT_WIDTH"] = "3840", ["APOLLO_CLIENT_HEIGHT"] = "2160" }).Active);

    [Fact]
    public void Stream1080p_IsInactive() =>
        Assert.False(BigPictureDetector.Detect(Desktop(), [Ultrawide()], Env("1920", "1080", "60")).Active);

    [Fact]
    public void StreamOff_IgnoresStreamClient()
    {
        var dev = Desktop(new BigPictureDef { Match = new BigPictureMatchDef { Stream = false } });
        Assert.False(BigPictureDetector.Detect(dev, [Ultrawide()], Env("3840", "2160")).Active);
    }

    [Fact]
    public void CustomPresetAndMode_AreReported()
    {
        var dev = Desktop(new BigPictureDef { Preset = "tv", Display = new DisplayModeDef { Width = 2560, Height = 1440, Refresh = 60 } });
        var r = BigPictureDetector.Detect(dev, [], Env("3840", "2160"));
        Assert.Equal("tv", r.Preset);
        Assert.Equal(2560, r.Mode.Width);
    }

    // ---------------------------------------------------------------- preset selection

    private readonly EngineFixture _f = new(WithBigPicture(EngineFixture.GameYaml));

    private static string WithBigPicture(string yaml) =>
        yaml.Replace("\r\n", "\n").Replace("  ally-x:\n    turbo:",
            "    big-picture:\n      extends: base\n      display: { width: 3840, height: 2160, refresh: 120 }\n  ally-x:\n    turbo:");

    public void Dispose() => _f.Dispose();

    private GameContext LoadWith(BigPictureResult r, PresetRef? preset = null, bool suppress = false, string device = "desktop") =>
        GameContext.Load(_f.Store, EngineFixture.GameId, _f.Device(device), preset, _f.Resolver,
            new BigPictureOptions { Suppress = suppress, Detect = _ => r });

    private static BigPictureResult Active(string? target = @"\.\DISPLAY2") =>
        new(true, "TV display 'X' 3840x2160", target, BigPictureResult.DefaultMode, "big-picture", false);

    [Fact]
    public void Active_PicksBigPicturePreset_OverRememberedSelection()
    {
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Recommended("quality"));
        var ctx = LoadWith(Active());
        Assert.Equal(PresetRef.Recommended("big-picture"), ctx.Preset);
        Assert.True(ctx.BigPictureUsed);
        Assert.Equal(@"\.\DISPLAY2", ctx.DisplayTarget);
    }

    [Fact]
    public void ExplicitPreset_Wins()
    {
        var ctx = LoadWith(Active(), PresetRef.Recommended("quality"));
        Assert.Equal(PresetRef.Recommended("quality"), ctx.Preset);
        Assert.False(ctx.BigPictureUsed);
        Assert.Null(ctx.DisplayTarget);
    }

    [Fact]
    public void Suppressed_OrOptedOut_KeepsNormalPreset()
    {
        Assert.False(LoadWith(Active(), suppress: true).BigPictureUsed);
        _f.Selections.SetNeverBigPicture("desktop", EngineFixture.GameId, true);
        var ctx = LoadWith(Active());
        Assert.False(ctx.BigPictureUsed);
        Assert.True(ctx.BigPictureOptedOut);
        _f.Selections.SetNeverBigPicture("desktop", EngineFixture.GameId, false);
        Assert.True(LoadWith(Active()).BigPictureUsed);
    }

    [Fact]
    public void Health_JudgesTheBigPictureRuns_WhenThePresetIsResolvedAsTheWrapperDoes()
    {
        // the wrapper records a TV session under the Big Picture preset; the page's health must filter by that same preset
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Recommended("performance"));
        var tv = LoadWith(Active());
        var recorded = tv.Preset!.ToString();
        Assert.Equal("recommended:big-picture", recorded);
        var start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        List<RunResult> runs = [.. Enumerable.Range(0, 3).Select(d => new RunResult
        {
            Utc = start.AddDays(d), Game = EngineFixture.GameId, Device = "desktop", Preset = recorded, Mode = RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = 1800, Capture = CaptureAnswer.NotAsked,
        })];
        HealthState For(GameContext ctx) => GameHealth.Evaluate(new HealthInputs
        {
            GameId = EngineFixture.GameId, Device = "desktop", HasConfig = true, HasPresets = true, Recent = runs, CurrentPreset = ctx.Preset?.ToString(),
        }).State;

        Assert.Equal(HealthState.Working, For(tv));
        // resolved without Big Picture (the old health path), the same runs were all thrown away
        Assert.Equal(HealthState.NotChecked, For(LoadWith(Active(), suppress: true)));
    }

    [Fact]
    public void OptOut_DoesNotDisturbRememberedPreset()
    {
        _f.Selections.Set("desktop", EngineFixture.GameId, PresetRef.Recommended("quality"));
        _f.Selections.SetNeverBigPicture("desktop", EngineFixture.GameId, true);
        Assert.Equal(PresetRef.Recommended("quality"), _f.Selections.Get("desktop", EngineFixture.GameId));
    }

    [Fact]
    public void GameWithoutBigPicturePreset_NotesAndKeepsPreset()
    {
        var ctx = LoadWith(Active(), device: "ally-x");
        Assert.False(ctx.BigPictureUsed);
        Assert.StartsWith("No Big Picture preset for Test Game; using ", ctx.BigPictureNote);
    }

    [Fact]
    public void Inactive_ChangesNothing()
    {
        var ctx = LoadWith(new BigPictureResult(false, "no", null, BigPictureResult.DefaultMode, "big-picture", false));
        Assert.Equal(PresetRef.Recommended("performance"), ctx.Preset);
        Assert.Null(ctx.BigPictureNote);
    }

    [Fact]
    public void Catalog_ListsBigPictureFirst_AndMarksAuto()
    {
        var ctx = LoadWith(Active());
        var list = PresetCatalog.Recommended(ctx.Game, ctx.Device, ctx.Preset, autoOnTv: true);
        Assert.Equal("big-picture", list[0].Ref.Name);
        Assert.Equal("Big Picture (TV)", list[0].Label);
        Assert.True(list[0].AutoOnTv);
        Assert.All(list.Skip(1), o => Assert.False(o.AutoOnTv));
    }

    [Theory]
    [InlineData("name: \"LG TV*\"")]
    [InlineData("name: [\"Sony*\", \"LG TV*\"]")]
    public void DevicesYaml_BigPictureBlock_ParsesAndMatches(string nameLine)
    {
        File.WriteAllText(_f.Paths.DevicesFile, $$"""
            devices:
              desktop:
                match: { gpu: "NVIDIA*" }
                display: { width: 5120, height: 2160, refresh: 165 }
                bigPicture:
                  preset: tv
                  display: { width: 3840, height: 2160, refresh: 120 }
                  match:
                    {{nameLine}}
                    minWidth: 3840
                    aspect: "16:9"
                    stream: false
            """);
        var dev = _f.Store.LoadDevices().Devices["desktop"];
        Assert.Equal("tv", dev.BigPicture!.Preset);
        Assert.False(dev.BigPicture.Match!.Stream);
        var r = BigPictureDetector.Detect(dev, [Ultrawide(), Tv()], NoEnv);
        Assert.True(r.Active);
        Assert.Equal("tv", r.Preset);
    }
}
