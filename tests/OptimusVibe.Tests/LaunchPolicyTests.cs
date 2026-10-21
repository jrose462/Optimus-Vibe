using OptimusVibe.Config;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

public sealed class LaunchPolicyTests
{
    [Theory]
    [InlineData("steam", 180)]
    [InlineData("other", 180)]
    [InlineData("ea", 600)]
    [InlineData("battlenet", 600)]
    [InlineData("epic", 600)]
    public void StartupTimeout_DefaultsByStore(string store, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), LaunchPolicy.StartupTimeout(new GameDef { Store = store }));

    [Fact]
    public void StartupTimeout_ExplicitWins() =>
        Assert.Equal(TimeSpan.FromSeconds(45), LaunchPolicy.StartupTimeout(new GameDef { Store = "ea", Launch = new LaunchDef { WaitSeconds = 45 } }));

    [Theory]
    [InlineData(null, true, "both")]
    [InlineData(null, false, "before")]
    [InlineData(null, null, "before")]
    [InlineData("afterProcessStart", true, "afterProcessStart")]
    [InlineData("BEFORE", true, "before")]
    [InlineData("nonsense", true, "both")]
    public void EffectiveTiming(string? configured, bool? cloud, string expected) =>
        Assert.Equal(expected, LaunchPolicy.EffectiveTiming(new GameDef { ApplyTiming = configured, CloudSyncsSettings = cloud }));

    [Fact]
    public void Timing_FlagsMatchTheMode()
    {
        Assert.True(LaunchPolicy.AppliesBefore(new GameDef { ApplyTiming = "both" }));
        Assert.True(LaunchPolicy.AppliesAfterStart(new GameDef { ApplyTiming = "both" }));
        Assert.False(LaunchPolicy.AppliesBefore(new GameDef { ApplyTiming = "afterProcessStart" }));
        Assert.False(LaunchPolicy.AppliesAfterStart(new GameDef { ApplyTiming = "before" }));
    }

    [Fact]
    public void MergeArgs_PresetWinsPerFlag_AndKeepsValues()
    {
        var merged = LaunchPolicy.MergeArgs(
            ["-skipStartScreen", "+com_adaptiveTickMaxHz", "60", "--exec=launch Pro"],
            ["+com_adaptiveTickMaxHz", "120", "-fpsClamp", "-1"]);
        Assert.Equal(["-skipStartScreen", "--exec=launch Pro", "+com_adaptiveTickMaxHz", "120", "-fpsClamp", "-1"], merged);
    }

    [Fact]
    public void MergeArgs_DedupesWithinAndHandlesNulls()
    {
        Assert.Equal(["-a", "-b"], LaunchPolicy.MergeArgs(["-a", "-A", "-b"], null));
        Assert.Empty(LaunchPolicy.MergeArgs(null, null));
        Assert.Equal(["--k=2"], LaunchPolicy.MergeArgs(["--k=1"], ["--k=2"]));
    }

    [Fact]
    public void AppendArgs_SkipsFlagsTheCommandAlreadyHas()
    {
        var cmd = LaunchPolicy.AppendArgs(["C:\\g\\game.exe", "-windowed", "+fps_max", "30"], ["-windowed", "+fps_max", "120", "-nostartupmovies"]);
        Assert.Equal(["C:\\g\\game.exe", "-windowed", "+fps_max", "30", "-nostartupmovies"], cmd);
    }

    [Fact]
    public void SteamUri_EncodesArguments()
    {
        Assert.Equal("steam://rungameid/42", LaunchPolicy.SteamUri(42, []));
        Assert.Equal("steam://rungameid/42//-skipStartScreen%20%2BcomMax%20120", LaunchPolicy.SteamUri(42, ["-skipStartScreen", "+comMax", "120"]));
        Assert.Equal("steam://rungameid/42//%22a%20b%22", LaunchPolicy.SteamUri(42, ["a b"]));
    }

    [Fact]
    public void WithArgs_OnlyTouchesPlainSteamUris()
    {
        Assert.Equal("steam://rungameid/7//-x", LaunchPolicy.WithArgs("steam://rungameid/7", ["-x"]));
        Assert.Equal("steam://rungameid/7//-y", LaunchPolicy.WithArgs("steam://rungameid/7//-y", ["-x"]));
        Assert.Equal("origin2://game/launch?offerIds=1", LaunchPolicy.WithArgs("origin2://game/launch?offerIds=1", ["-x"]));
        Assert.Equal("steam://rungameid/7", LaunchPolicy.WithArgs("steam://rungameid/7", []));
    }

    [Fact]
    public void ResolveLaunch_SteamUriRoute_CarriesTheArgs()
    {
        var game = new GameDef { Id = "g", SteamAppId = 99 };
        Assert.Equal(["steam://rungameid/99//-x"], Runner.ResolveLaunch(game, [], null, ["-x"]));
        Assert.Equal(["steam://rungameid/99"], Runner.ResolveLaunch(game, [], null, null)); // mapping run: nothing added
    }

    [Fact]
    public void ResolveLaunch_WrappedCommand_GetsArgsAfterIt()
    {
        var game = new GameDef { Id = "g", SteamAppId = 99 };
        Assert.Equal(["C:\\g\\game.exe", "+x", "1"], Runner.ResolveLaunch(game, ["C:\\g\\game.exe"], null, ["+x", "1"]));
    }

    [Fact]
    public void ResolveLaunch_ExeRoute_KeepsLaunchArgsEvenInMappingRuns()
    {
        var game = new GameDef { Id = "g", Launch = new LaunchDef { Exe = "C:\\Launcher\\l.exe", Args = ["--exec=launch Pro"] } };
        Assert.Equal(["C:\\Launcher\\l.exe", "--exec=launch Pro"], Runner.ResolveLaunch(game, [], null, null));
        Assert.Equal(["C:\\Launcher\\l.exe", "--exec=launch Pro", "-x"], Runner.ResolveLaunch(game, [], null, ["--exec=launch Pro", "-x"]));
    }

    [Fact]
    public void ResolveLaunch_NothingToLaunch_Throws() =>
        Assert.Throws<ConfigException>(() => Runner.ResolveLaunch(new GameDef { Id = "g" }, [], null, null));

    [Fact]
    public void ResolveLaunch_LibraryExe_GetsPresetArgsAppended()
    {
        var game = new GameDef { Id = "g" };
        var line = Runner.ResolveLaunch(game, [], new RunOptions { Launch = "C:\\g\\g.exe", LaunchArgs = ["-a"] }, ["-b"]);
        Assert.Equal(["C:\\g\\g.exe", "-a", "-b"], line);
    }

    // ---------------------------------------------------------------- preset blocks

    private static GameDef GameWithPreset(Dictionary<string, object?> body, params string[] fileIds)
    {
        var game = new GameDef { Id = "g", Files = fileIds.Select(i => new FileDef { Id = i, Handler = "ini" }).ToList() };
        game.Presets["desktop"] = new() { ["p"] = body };
        return game;
    }

    [Fact]
    public void PresetLaunchArgs_AreResolved()
    {
        var game = GameWithPreset(new() { ["launch"] = new Dictionary<string, object?> { ["args"] = new List<object?> { "+x", 120, "" } } }, "user");
        var p = ProfileResolver.ResolvePreset(game, "desktop", null, "p");
        Assert.Equal(["+x", "120"], p.LaunchArgs);
        Assert.Empty(p.FileBlocks);
    }

    [Fact]
    public void PresetLaunch_BadShapes_Throw()
    {
        var game = GameWithPreset(new() { ["launch"] = "oops" }, "user");
        Assert.Throws<ConfigException>(() => ProfileResolver.ResolvePreset(game, "desktop", null, "p"));
        game = GameWithPreset(new() { ["launch"] = new Dictionary<string, object?> { ["exe"] = "x" } }, "user");
        Assert.Throws<ConfigException>(() => ProfileResolver.ResolvePreset(game, "desktop", null, "p"));
    }

    [Fact]
    public void FileNamedLaunch_StillWorksAsAFileBlock()
    {
        var game = GameWithPreset(new() { ["launch"] = new Dictionary<string, object?> { ["S"] = new Dictionary<string, object?> { ["K"] = "1" } } }, "launch");
        var p = ProfileResolver.ResolvePreset(game, "desktop", null, "p");
        Assert.Empty(p.LaunchArgs);
        Assert.True(p.FileBlocks.ContainsKey("launch"));
    }

    [Fact]
    public void DisplayKeep_IsExplicitOnly()
    {
        var dev = new DeviceDef { Display = new DisplayModeDef { Width = 1920, Height = 1080, Refresh = 120 } };
        var keep = ProfileResolver.ResolvePreset(GameWithPreset(new() { ["display"] = "keep" }, "user"), "desktop", dev, "p");
        Assert.True(keep.DisplayKeep);
        Assert.Null(keep.Display);
        var none = ProfileResolver.ResolvePreset(GameWithPreset(new() { ["display"] = "none" }, "user"), "desktop", dev, "p");
        Assert.False(none.DisplayKeep);
        var omitted = ProfileResolver.ResolvePreset(GameWithPreset(new() { ["label"] = "x" }, "user"), "desktop", dev, "p");
        Assert.False(omitted.DisplayKeep);
    }

    // ---------------------------------------------------------------- words

    [Fact]
    public void Describe_ReflectsArchetypeAndVerification()
    {
        var profile = new ResolvedProfile
        {
            Device = "desktop", Source = PresetRef.Recommended("p"),
            FileBlocks = { ["user"] = new Dictionary<string, object?> { ["S"] = null } },
        };
        var held = new RunResult { Mode = RunResult.VerifyMode, AppliedKeys = 3, Utc = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc) };
        var overridden = new RunResult { Mode = RunResult.VerifyMode, AppliedKeys = 3, Drift = [new RunDrift { FileId = "user", Key = "K" }] };

        Assert.Contains("unverified", LaunchPolicy.Describe(new GameDef(), profile, null));
        Assert.Contains("verified held", LaunchPolicy.Describe(new GameDef(), profile, held));
        Assert.Contains("overrode 1", LaunchPolicy.Describe(new GameDef(), profile, overridden));
        Assert.Contains("snapshot", LaunchPolicy.Describe(new GameDef { Archetype = "binary-truth" }, profile, null));
        Assert.StartsWith("In-game steps only", LaunchPolicy.Describe(new GameDef { Mappable = false }, profile, held));
        Assert.StartsWith("No settings are written", LaunchPolicy.Describe(new GameDef(), new ResolvedProfile { Device = "d", Source = PresetRef.Recommended("p") }, null));
    }

    [Fact]
    public void RunResult_OldJsonWithoutNewFields_StillLoads()
    {
        using var tmp = new TempDir();
        File.WriteAllText(RunResults.FileFor(tmp.Path),
            """{"games":{"g":{"desktop":{"last":{"game":"g","device":"desktop","preset":"recommended:p","appliedKeys":2}}}}}""");
        var r = RunResults.Get(tmp.Path, "g", "desktop");
        Assert.NotNull(r);
        Assert.Null(r!.LaunchCommand);
        Assert.Empty(r.Reapplied);
    }
}
