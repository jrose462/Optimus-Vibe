using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>
/// <see cref="GameHealth.Evaluate"/>: one state per game and device, first match in the order Flagged > NoPresets > CantBeSetFromOutside >
/// GameUpdated > GameChangedSettings > SyncUnsure > Working > NotChecked, each with one status line, one why line and at most one action.
/// </summary>
public sealed class GameHealthTests
{
    private const string Preset = "recommended:performance";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static HealthInputs In() => new()
    {
        GameId = "g", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = Preset,
    };

    private static RunResult Run(int day, int drifted = 0, bool verify = false, CaptureAnswer? capture = null, int? sec = 1800,
        string preset = Preset, string now = "Low")
    {
        var r = new RunResult
        {
            Utc = T0.AddDays(day), Game = "g", Device = "desktop", Preset = preset, Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = verify ? null : sec, UserChanged = verify ? false : null,
            Capture = verify ? null : capture ?? (drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked),
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Quality{i}", Applied = "High", Now = now });
        return r;
    }

    private static LabIssue Issue(LabStatus status, bool open = true) =>
        new(12, "Request presets: Game", "https://github.com/owner/config/issues/12", open, [], status, null, null, null);

    private static FlagDef Flag() => GameFlags.Create("the game rewrites its settings on every boot", issue: 7, today: T0);

    // ---------------------------------------------------------------- flagged

    [Fact]
    public void Flagged_ByTheUser_ShowsTheFlagsStatusLine_AndSuggestsNothing()
    {
        var flag = Flag();
        var h = GameHealth.Evaluate(In() with { Flag = flag, HasUserFlag = true, OverriddenStreak = 3, Recent = [Run(0, drifted: 8)] });
        Assert.Equal(HealthState.Flagged, h.State);
        Assert.Equal(GameFlags.StatusLine(flag), h.Headline);
        Assert.Null(h.Why);
        Assert.Equal(HealthAction.None, h.Suggested);
        Assert.Null(h.FlagReason); // flagged already: never suggested again
    }

    [Fact]
    public void Flagged_WinsOverMissingPresets()
    {
        var h = GameHealth.Evaluate(In() with { HasPresets = false, Flag = Flag(), HasUserFlag = true });
        Assert.Equal(HealthState.Flagged, h.State);
    }

    [Fact]
    public void Flagged_ByTheGameConfigOnly_SaysOnlyTheLabCanLiftIt_EvenWhenResearchFinished()
    {
        var h = GameHealth.Evaluate(In() with { Flag = Flag(), FlagFromConfig = true, ResearchFinished = true });
        Assert.Equal(HealthState.Flagged, h.State);
        Assert.Equal(GameHealth.ConfigFlagLine, h.Why);
        Assert.Equal(HealthAction.None, h.Suggested);
    }

    [Fact]
    public void Flagged_ByTheUser_ResearchFinished_SuggestsManagingSettingsAgain()
    {
        var h = GameHealth.Evaluate(In() with { Flag = Flag(), HasUserFlag = true, ResearchFinished = true });
        Assert.Equal(HealthAction.ManageAgain, h.Suggested);
        Assert.Equal(GameHealth.ResearchFinishedLine, h.Why);
    }

    // ---------------------------------------------------------------- no presets

    [Fact]
    public void NoPresets_LabReady_SuggestsRequestingThem()
    {
        var h = GameHealth.Evaluate(In() with { HasConfig = false, HasPresets = false });
        Assert.Equal(HealthState.NoPresets, h.State);
        Assert.Equal("No presets for this device yet", h.Headline);
        Assert.Equal(HealthAction.RequestPresets, h.Suggested);
        Assert.False(h.LabWorking);
    }

    [Fact]
    public void NoPresets_WithoutTheLab_SuggestsNothing_ThePanelAlreadyLinksToSettings()
    {
        var h = GameHealth.Evaluate(In() with { HasPresets = false, LabReady = false });
        Assert.Equal(HealthState.NoPresets, h.State);
        Assert.Equal(HealthAction.None, h.Suggested);
        Assert.Null(h.Why);
    }

    [Fact]
    public void NoPresets_OpenRequest_SaysTheLabIsWorkingOnIt()
    {
        var h = GameHealth.Evaluate(In() with { HasPresets = false, LabRequest = Issue(LabStatus.Researching) });
        Assert.Equal(HealthState.NoPresets, h.State);
        Assert.Equal("Game lab is working on it (#12, researching)", h.Headline);
        Assert.True(h.LabWorking);
        Assert.Equal(HealthAction.None, h.Suggested); // the chip row already has Open issue
    }

    [Fact]
    public void NoPresets_LabNeedsEvidence_AndNoMapping_SuggestsMappingTheGame()
    {
        var h = GameHealth.Evaluate(In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence) });
        Assert.Equal(HealthAction.MapGame, h.Suggested);
        Assert.Contains("needs evidence", h.Headline);

        var mapped = GameHealth.Evaluate(In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence), Mapping = new ProbeRecord { Evidence = "x" } });
        Assert.Equal(HealthAction.None, mapped.Suggested);

        var notHere = GameHealth.Evaluate(In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence), CanLaunch = false });
        Assert.Equal(HealthAction.None, notHere.Suggested);
    }

    [Fact]
    public void NoPresets_AClosedRequest_IsNotWorking()
    {
        var h = GameHealth.Evaluate(In() with { HasPresets = false, LabRequest = Issue(LabStatus.Done, open: false) });
        Assert.Equal("No presets for this device yet", h.Headline);
        Assert.Equal(HealthAction.RequestPresets, h.Suggested);
    }

    // ---------------------------------------------------------------- can't be set from outside (suggested, never flagged)

    [Fact]
    public void TwoOverriddenTests_SuggestTheFlag()
    {
        var h = GameHealth.Evaluate(In() with { OverriddenStreak = 2 });
        Assert.Equal(HealthState.CantBeSetFromOutside, h.State);
        Assert.Equal(HealthAction.FlagSetInGame, h.Suggested);
        Assert.Equal(GameFlags.VerifySuggestionReason(2), h.FlagReason);
        Assert.Contains(h.FlagReason!, h.Why);
    }

    [Fact]
    public void TwoPassivelyOverriddenLaunches_SuggestTheFlag()
    {
        var h = GameHealth.Evaluate(In() with { PassiveOverriddenStreak = 2 });
        Assert.Equal(HealthState.CantBeSetFromOutside, h.State);
        Assert.Equal(GameFlags.PassiveSuggestionReason(2), h.FlagReason);
    }

    [Fact]
    public void OneOverriddenTest_DoesNotSuggestTheFlag()
    {
        var h = GameHealth.Evaluate(In() with { OverriddenStreak = 1, PassiveOverriddenStreak = 1 });
        Assert.NotEqual(HealthState.CantBeSetFromOutside, h.State);
        Assert.Null(h.FlagReason);
    }

    [Fact]
    public void AMappingRunWithOnlyBinaryChanges_SuggestsTheFlag()
    {
        var mapping = new ProbeRecord { SuggestSetInGame = true, SuggestReason = "only system.save changed", OpaqueFiles = ["{LocalAppData}/Game/system.save"] };
        var h = GameHealth.Evaluate(In() with { Mapping = mapping, ConfiguredFiles = ["{Documents}/Game/settings.ini"] });
        Assert.Equal(HealthState.CantBeSetFromOutside, h.State);
        Assert.Equal("only system.save changed", h.FlagReason);
    }

    [Fact]
    public void AMappingRun_WhoseBinaryFilesTheConfigAlreadyWrites_DoesNotSuggestTheFlag()
    {
        // a GVAS .sav is "binary" to the mapping run, but the gvas handler writes its keys
        var mapping = new ProbeRecord
        {
            SuggestSetInGame = true, SuggestReason = "only GameUserSettings.sav changed",
            OpaqueFiles = ["{LocalAppData}\\Game\\Saved\\SaveGames\\GameUserSettings.sav"],
        };
        var inputs = In() with { Mapping = mapping, ConfiguredFiles = ["{LocalAppData}/Game/Saved/SaveGames/GameUserSettings.sav"] };
        Assert.Null(GameHealth.FlagSuggestion(inputs));
        Assert.NotEqual(HealthState.CantBeSetFromOutside, GameHealth.Evaluate(inputs).State);

        // one binary file the config doesn't know is enough to suggest it again
        var more = inputs with { Mapping = new ProbeRecord { SuggestSetInGame = true, OpaqueFiles = [.. mapping.OpaqueFiles, "{LocalAppData}/Game/other.bin"] } };
        Assert.NotNull(GameHealth.FlagSuggestion(more));
    }

    [Fact]
    public void FlagSuggestion_NeedsAConfig()
    {
        Assert.Null(GameHealth.FlagSuggestion(In() with { HasConfig = false, HasPresets = false, OverriddenStreak = 2 }));
    }

    [Fact]
    public void CantBeSetFromOutside_WinsOverAGameUpdate()
    {
        var h = GameHealth.Evaluate(In() with
        {
            OverriddenStreak = 2, Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2",
        });
        Assert.Equal(HealthState.CantBeSetFromOutside, h.State);
    }

    // ---------------------------------------------------------------- game updated

    [Fact]
    public void GameUpdated_SuggestsAskingTheLabToReCheck()
    {
        var updated = In() with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" };
        var h = GameHealth.Evaluate(updated);
        Assert.Equal(HealthState.GameUpdated, h.State);
        Assert.Equal("Game updated since these presets were made", h.Headline);
        Assert.Equal(HealthAction.RefreshPresets, h.Suggested);
        Assert.Equal("Ask the game lab to re-check", GameHealth.ActionText(h.Suggested));

        Assert.Equal(HealthAction.TestPreset, GameHealth.Evaluate(updated with { LabReady = false }).Suggested);
        Assert.Equal(HealthAction.TestPreset, GameHealth.Evaluate(updated with { LabRequest = Issue(LabStatus.Researching) }).Suggested);
        Assert.Equal(HealthAction.None, GameHealth.Evaluate(updated with { LabReady = false, CanLaunch = false }).Suggested);
    }

    [Fact]
    public void GameUpdated_WinsOverChangedSettings()
    {
        var h = GameHealth.Evaluate(In() with
        {
            Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2", LastVerify = Run(1, drifted: 3, verify: true),
        });
        Assert.Equal(HealthState.GameUpdated, h.State);
    }

    [Fact]
    public void SameBuild_IsNotAnUpdate()
    {
        var h = GameHealth.Evaluate(In() with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:1" });
        Assert.Equal(HealthState.NotChecked, h.State);
    }

    // ---------------------------------------------------------------- game changed settings

    [Fact]
    public void TheSameDriftTwice_IsTheGamesDoing()
    {
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)] });
        Assert.Equal(HealthState.GameChangedSettings, h.State);
        Assert.Equal("The game changed some settings (4 of 10)", h.Headline);
        Assert.StartsWith("Last launch, ", h.Why);
        Assert.Contains("Quality0", h.Why);
        Assert.Equal(HealthAction.TestPreset, h.Suggested);
    }

    [Fact]
    public void AnOverriddenTest_SuggestsAskingTheLab()
    {
        var h = GameHealth.Evaluate(In() with { LastVerify = Run(0, drifted: 3, verify: true) });
        Assert.Equal(HealthState.GameChangedSettings, h.State);
        Assert.StartsWith("Last test, ", h.Why);
        Assert.Equal(HealthAction.RefreshPresets, h.Suggested);
    }

    [Fact]
    public void MostOfThePresetDrifted_IsTheOldHint_UnlessKeptAsASave()
    {
        // the player was never asked (prompt off): passively unclear, but the old "Test preset?" hint still applies
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0, drifted: 8, capture: CaptureAnswer.Off)] });
        Assert.Equal(HealthState.GameChangedSettings, h.State);

        var saved = GameHealth.Evaluate(In() with { Recent = [Run(0, drifted: 8, capture: CaptureAnswer.Saved)] });
        Assert.Equal(HealthState.NotChecked, saved.State);
    }

    [Fact]
    public void ALittleUnaskedDrift_IsNotBlamedOnTheGame()
    {
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0, drifted: 2, capture: CaptureAnswer.Off)] });
        Assert.Equal(HealthState.NotChecked, h.State);
    }

    [Fact]
    public void AHeldLaunchAfterAnOverriddenTest_ClearsIt()
    {
        var h = GameHealth.Evaluate(In() with { LastVerify = Run(0, drifted: 3, verify: true), Recent = [Run(1)] });
        Assert.Equal(HealthState.NotChecked, h.State);
        Assert.Contains("last launch", h.Why);
    }

    [Fact]
    public void AHeldTestAfterAnOverriddenLaunch_ClearsIt()
    {
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0, drifted: 8, capture: CaptureAnswer.Off)], LastVerify = Run(1, verify: true) });
        Assert.Equal(HealthState.Working, h.State);
    }

    [Fact]
    public void ChangedSettings_WinOverSyncDoubts()
    {
        var h = GameHealth.Evaluate(In() with
        {
            LastVerify = Run(0, drifted: 3, verify: true),
            CloudSync = new CloudSyncReport { GameId = "g", Device = "desktop", Contradiction = "Declared not synced, but it is" },
        });
        Assert.Equal(HealthState.GameChangedSettings, h.State);
    }

    // ---------------------------------------------------------------- sync unsure

    [Fact]
    public void ACloudSyncContradiction_IsSyncUnsure_WithItAsTheWhy()
    {
        var h = GameHealth.Evaluate(In() with
        {
            Recent = [Run(0), Run(1), Run(2)],
            CloudSync = new CloudSyncReport { GameId = "g", Device = "desktop", Contradiction = "Researched as per machine, but Steam Cloud syncs it" },
        });
        Assert.Equal(HealthState.SyncUnsure, h.State);
        Assert.Equal("Settings may be changed by cloud sync", h.Headline);
        Assert.Equal("Researched as per machine, but Steam Cloud syncs it.", h.Why);
        Assert.Equal(HealthAction.None, h.Suggested);
    }

    [Fact]
    public void ExternalChanges_AreSyncUnsure_OnlyWhileConfidenceIsLow()
    {
        CloudSyncReport Report(SyncConfidence c) => new() { GameId = "g", Device = "desktop", HasExternalChanges = true, Confidence = c };
        Assert.Equal(HealthState.SyncUnsure, GameHealth.Evaluate(In() with { CloudSync = Report(SyncConfidence.Low) }).State);
        Assert.Equal(HealthState.NotChecked, GameHealth.Evaluate(In() with { CloudSync = Report(SyncConfidence.Medium) }).State);
    }

    [Fact]
    public void ALaunchWhereOnlyCloudSyncRewritesDrifted_IsSyncUnsure()
    {
        var run = Run(0, drifted: 2);
        run.SyncReapplied = ["gfx"];
        var h = GameHealth.Evaluate(In() with { Recent = [run] });
        Assert.Equal(HealthState.SyncUnsure, h.State);
        Assert.Contains("cloud sync", h.Why);
    }

    // ---------------------------------------------------------------- working / not checked

    [Fact]
    public void ThreeHeldLaunches_AreWorking()
    {
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0), Run(1), Run(2)] });
        Assert.Equal(HealthState.Working, h.State);
        Assert.Equal("Working. Presets held on your last 3 launches.", h.Headline);
        Assert.StartsWith("Last checked ", h.Why);
        Assert.Equal(HealthAction.None, h.Suggested);
    }

    [Fact]
    public void AHeldTest_IsWorking_AndSaysWhenItWasVerified()
    {
        var h = GameHealth.Evaluate(In() with { LastVerify = Run(0, verify: true) });
        Assert.Equal(HealthState.Working, h.State);
        Assert.StartsWith("Verified on this device, ", h.Why);
    }

    [Fact]
    public void RunsOfAnotherPreset_DontCount()
    {
        const string other = "recommended:quality";
        var h = GameHealth.Evaluate(In() with
        {
            Recent = [Run(0, preset: other), Run(1, preset: other), Run(2, preset: other)], LastVerify = Run(3, verify: true, preset: other),
        });
        Assert.Equal(HealthState.NotChecked, h.State);

        // and a run of another preset in between ends the streak
        var broken = GameHealth.Evaluate(In() with { Recent = [Run(0), Run(1), Run(2, preset: other), Run(3)] });
        Assert.Equal(HealthState.NotChecked, broken.State);
    }

    [Fact]
    public void NoRuns_AreNotChecked_AndSuggestNothing()
    {
        var h = GameHealth.Evaluate(In());
        Assert.Equal(HealthState.NotChecked, h.State);
        Assert.Equal("Not checked on this device yet", h.Headline);
        Assert.Equal(HealthAction.None, h.Suggested);
        Assert.False(h.NeedsAttention);
    }

    [Fact]
    public void TwoHeldLaunches_SayHowManyMore()
    {
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0), Run(1)] });
        Assert.Equal(HealthState.NotChecked, h.State);
        Assert.Contains("2 launches", h.Why);
        Assert.Contains("1 more", h.Why);
    }

    [Fact]
    public void LaunchesThatStayUnclear_SuggestATest()
    {
        // short sessions with no drift: not long enough to count as held
        var unclear = In() with { Recent = [Run(0, sec: 20), Run(1, sec: 20), Run(2, sec: 20)] };
        Assert.Equal(HealthAction.TestPreset, GameHealth.Evaluate(unclear).Suggested);
        Assert.Equal(HealthAction.None, GameHealth.Evaluate(unclear with { CanLaunch = false }).Suggested);
    }

    [Fact]
    public void AnOpenRequest_IsASecondaryChip_WhenPresetsExist()
    {
        var h = GameHealth.Evaluate(In() with { Recent = [Run(0), Run(1), Run(2)], LabRequest = Issue(LabStatus.Researching) });
        Assert.Equal(HealthState.Working, h.State);
        Assert.True(h.LabWorking);
    }

    // ---------------------------------------------------------------- precedence, words

    [Fact]
    public void Precedence_FirstMatchWins()
    {
        // every signal at once, then peeled off one at a time
        var all = In() with
        {
            Flag = Flag(), HasUserFlag = true, HasPresets = false, OverriddenStreak = 2,
            Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2",
            LastVerify = Run(5, drifted: 3, verify: true),
            CloudSync = new CloudSyncReport { GameId = "g", Device = "desktop", Contradiction = "x" },
            Recent = [Run(0), Run(1), Run(2)],
        };
        Assert.Equal(HealthState.Flagged, GameHealth.Evaluate(all).State);
        all = all with { Flag = null, HasUserFlag = false };
        Assert.Equal(HealthState.NoPresets, GameHealth.Evaluate(all).State);
        all = all with { HasPresets = true };
        Assert.Equal(HealthState.CantBeSetFromOutside, GameHealth.Evaluate(all).State);
        all = all with { OverriddenStreak = 0 };
        Assert.Equal(HealthState.GameUpdated, GameHealth.Evaluate(all).State);
        all = all with { CurrentBuild = "steam:1" };
        Assert.Equal(HealthState.GameChangedSettings, GameHealth.Evaluate(all).State);
        all = all with { LastVerify = null };
        Assert.Equal(HealthState.SyncUnsure, GameHealth.Evaluate(all).State);
        all = all with { CloudSync = null };
        Assert.Equal(HealthState.Working, GameHealth.Evaluate(all).State);
        all = all with { Recent = [] };
        Assert.Equal(HealthState.NotChecked, GameHealth.Evaluate(all).State);
    }

    [Fact]
    public void AttentionStates_AreTheOnesWorthATileChip()
    {
        Assert.True(GameHealth.Evaluate(In() with { LastVerify = Run(0, drifted: 3, verify: true) }).NeedsAttention);
        Assert.True(GameHealth.Evaluate(In() with { OverriddenStreak = 2 }).NeedsAttention);
        Assert.False(GameHealth.Evaluate(In() with { Recent = [Run(0), Run(1), Run(2)] }).NeedsAttention);
    }

    [Fact]
    public void EveryAction_HasButtonText()
    {
        foreach (var a in Enum.GetValues<HealthAction>())
            Assert.Equal(a == HealthAction.None, GameHealth.ActionText(a).Length == 0);
    }
}
