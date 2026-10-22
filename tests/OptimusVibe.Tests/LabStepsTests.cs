using System.Globalization;
using OptimusVibe.CloudSync;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;
using OptimusVibe.Library;

namespace OptimusVibe.Tests;

/// <summary>
/// <see cref="LabSteps.Build"/>: the guided "Step X of N" checklist. Steps that do not apply are left out (N is dynamic), the first step not
/// done is the current one, the health's rules (flag, missing presets, updates, changed settings, cloud sync) decide which step that is.
/// </summary>
public sealed class LabStepsTests
{
    private const string Preset = "recommended:performance";
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static HealthInputs In() => new()
    {
        GameId = "g", Device = "desktop", HasConfig = true, HasPresets = true, LabReady = true, CanLaunch = true, CurrentPreset = Preset,
    };

    private static RunResult Run(int day, int drifted = 0, bool verify = false, CaptureAnswer? capture = null, int? sec = 1800,
        string preset = Preset, string? build = null)
    {
        var r = new RunResult
        {
            Utc = T0.AddDays(day), Game = "g", Device = "desktop", Preset = preset, Mode = verify ? RunResult.VerifyMode : RunResult.NormalMode,
            AppliedKeys = 10, ProcessSeen = true, DurationSec = verify ? null : sec, UserChanged = verify ? false : null, Build = build,
            Capture = verify ? null : capture ?? (drifted > 0 ? CaptureAnswer.DontSave : CaptureAnswer.NotAsked),
        };
        for (var i = 0; i < drifted; i++) r.Drift.Add(new RunDrift { FileId = "gfx", Key = $"Quality{i}", Applied = "High", Now = "Low" });
        return r;
    }

    /// <summary>A launch the passive check rates overridden on its own: most of the preset drifted in a short, unsaved session.</summary>
    private static RunResult Overridden(int day, string? build = null) => Run(day, drifted: 6, sec: 120, build: build);

    private static LabIssue Issue(LabStatus status) => new(12, "Request presets: Game", "https://github.com/owner/config/issues/12", true, [], status, null, null, null);

    private static string Day(DateTime utc) => utc.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);

    private static CloudSyncReport UnknownSync() => new() { GameId = "g", Device = "desktop" };
    private static CloudSyncReport PerMachine() => new() { GameId = "g", Device = "desktop", Declared = false };

    private static CloudSyncReport ChangedBehindOurBack() =>
        new() { GameId = "g", Device = "desktop", HasExternalChanges = true, Confidence = SyncConfidence.Low };

    private static HealthInputs Held() => In() with { Recent = [Run(0), Run(1), Run(2)] };

    private static void AssertStates(LabJourney j, params StepState[] expected) =>
        Assert.Equal(expected, j.Steps.Select(s => s.State).ToArray());

    private static void AssertIds(LabJourney j, params LabStepId[] expected) =>
        Assert.Equal(expected, j.Steps.Select(s => s.Id).ToArray());

    // ---------------------------------------------------------------- step 1: get presets

    [Fact]
    public void NoPresets_FirstStepIsCurrent_AndRequestsThem()
    {
        var j = LabSteps.Build(In() with { HasConfig = false, HasPresets = false });
        Assert.Equal(LabJourneyKind.Steps, j.Kind);
        AssertIds(j, LabStepId.GetPresets, LabStepId.PlayOnce, LabStepId.ConfirmKept);
        AssertStates(j, StepState.Current, StepState.Upcoming, StepState.Upcoming);
        Assert.Equal(0, j.CurrentIndex);
        Assert.Equal(3, j.Count);
        Assert.Equal("Step 1 of 3", j.ProgressText);
        Assert.Equal("Get presets", j.Steps[0].Title);
        Assert.Equal(LabStepAction.RequestPresets, j.Steps[0].Primary);
        Assert.Empty(j.Steps[0].Secondary);
        // only the current step has buttons
        Assert.All(j.Steps.Skip(1), s => Assert.Equal(LabStepAction.None, s.Primary));
    }

    [Fact]
    public void NoPresets_WithoutTheLab_HasNoButton()
    {
        var j = LabSteps.Build(In() with { HasPresets = false, LabReady = false });
        Assert.Equal(StepState.Current, j.Steps[0].State);
        Assert.Equal(LabStepAction.None, j.Steps[0].Primary);
    }

    [Fact]
    public void LabWorking_SaysSo_AndOffersNoRequest()
    {
        var j = LabSteps.Build(In() with { HasPresets = false, LabRequest = Issue(LabStatus.Researching) });
        Assert.Equal("Step 1 of 3", j.ProgressText);
        Assert.Contains("working on it", j.Steps[0].Detail);
        Assert.Contains("#12", j.Steps[0].Detail);
        Assert.Equal(LabStepAction.None, j.Steps[0].Primary);
        Assert.Empty(j.Steps[0].Secondary);
    }

    [Fact]
    public void LabWorking_NeedsEvidence_OffersMappingTheGame()
    {
        var j = LabSteps.Build(In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence) });
        Assert.Equal(LabStepAction.None, j.Steps[0].Primary);
        Assert.Equal([LabStepAction.MapThisGame], j.Steps[0].Secondary);

        var mapped = LabSteps.Build(In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence), Mapping = new ProbeRecord { Utc = T0 } });
        Assert.Empty(mapped.Steps[0].Secondary); // the evidence is on its way already
    }

    // ---------------------------------------------------------------- step 2: play once

    [Fact]
    public void PresetsButNeverPlayed_PlayOnceIsCurrent_AndPlaysNormally()
    {
        var j = LabSteps.Build(In());
        AssertIds(j, LabStepId.GetPresets, LabStepId.PlayOnce, LabStepId.ConfirmKept);
        AssertStates(j, StepState.Done, StepState.Current, StepState.Upcoming);
        Assert.Equal(1, j.CurrentIndex);
        Assert.Equal("Step 2 of 3", j.ProgressText);
        var play = j.Steps[1];
        Assert.Equal("Play once with the preset", play.Title);
        Assert.Equal("Play normally and quit when you like. Optimus-Vibe checks afterwards whether the game kept the settings.", play.Detail);
        Assert.Equal(LabStepAction.Play, play.Primary);
        Assert.Empty(play.Secondary); // Test preset stays a manual tool
        Assert.StartsWith("Got presets", j.Steps[0].DoneSummary);
        Assert.Equal("", j.Steps[1].DoneSummary);
    }

    [Fact]
    public void PlayOnce_NeedsAnInstalledGame()
    {
        var j = LabSteps.Build(In() with { CanLaunch = false });
        Assert.Equal(LabStepId.PlayOnce, j.Current!.Id);
        Assert.Equal(LabStepAction.None, j.Current.Primary);
    }

    [Fact]
    public void PresetsArrivedDate_ShowsInTheDoneSummary()
    {
        var j = LabSteps.Build(In() with { Presets = new PresetsMark { MadeUtc = T0 } });
        Assert.Equal($"Got presets for this device · {Day(T0)}", j.Steps[0].DoneSummary);
    }

    [Fact]
    public void RunsOfAnotherPreset_DontCount()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0, preset: "recommended:quality")] });
        Assert.Equal(LabStepId.PlayOnce, j.Current!.Id);
        AssertStates(j, StepState.Done, StepState.Current, StepState.Upcoming);
    }

    // ---------------------------------------------------------------- step 3: confirm the game kept them

    [Fact]
    public void PlayedAndHeld_IsAllSet()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0)] });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
        AssertStates(j, StepState.Done, StepState.Done, StepState.Done);
        Assert.Equal(-1, j.CurrentIndex);
        Assert.Null(j.Current);
        Assert.Equal("All set", j.Headline);
        Assert.Equal("Working. Optimus-Vibe keeps watching for game updates.", j.Detail);
        Assert.Equal("All set", j.ProgressText);
        Assert.Equal($"Played once with the preset · {Day(Run(0).Utc)}", j.Steps[1].DoneSummary);
        Assert.Equal($"The game kept the settings · {Day(Run(0).Utc)}", j.Steps[2].DoneSummary);
        Assert.All(j.Steps, s => Assert.Equal(LabStepAction.None, s.Primary));
    }

    [Fact]
    public void Working_ThreeHeldLaunches_IsAllSet()
    {
        Assert.Equal(LabJourneyKind.AllSet, LabSteps.Build(Held()).Kind);
    }

    [Fact]
    public void AHeldTestPreset_CountsAsPlayedAndKept()
    {
        var j = LabSteps.Build(In() with { LastVerify = Run(0, verify: true) });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
    }

    [Fact]
    public void PlayedButUnclear_ConfirmIsCurrent_AndAsksToPlayAgain()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0, sec: 20)] });
        Assert.Equal(LabJourneyKind.Steps, j.Kind);
        AssertStates(j, StepState.Done, StepState.Done, StepState.Current);
        Assert.Equal("Step 3 of 3", j.ProgressText);
        Assert.Equal("Confirm the game kept them", j.Current!.Title);
        Assert.Equal(LabStepAction.Play, j.Current.Primary);
    }

    [Fact]
    public void UnclearLaunchAfterAHeldOne_IsNotKept()
    {
        // the last relevant run decides
        var j = LabSteps.Build(In() with { Recent = [Run(0), Run(1, sec: 20)] });
        Assert.Equal(LabStepId.ConfirmKept, j.Current!.Id);
    }

    // ---------------------------------------------------------------- the game changed settings: the fix

    [Fact]
    public void GameChangedSettings_WithoutMapping_FixStepMapsTheGame()
    {
        var run = Run(1, drifted: 4);
        var j = LabSteps.Build(In() with { Recent = [Run(0, drifted: 4), run] });
        AssertIds(j, LabStepId.GetPresets, LabStepId.PlayOnce, LabStepId.ConfirmKept);
        AssertStates(j, StepState.Done, StepState.Done, StepState.Current);
        var fix = j.Current!;
        Assert.True(fix.IsFix);
        Assert.Equal("Fix: the game changed 4 of 10 settings", fix.Title);
        Assert.Equal(RunResults.Names(run, 3), fix.Detail);
        Assert.Equal("Quality0, Quality1, Quality2 and 1 more.", fix.Detail);
        Assert.Equal(LabStepAction.MapThisGame, fix.Primary);
        Assert.Equal([LabStepAction.KeepAsManualSave], fix.Secondary);
        Assert.Same(run, j.ChangedRun);
        Assert.Equal("Step 3 of 3", j.ProgressText);
    }

    [Fact]
    public void GameChangedSettings_WithMappingEvidence_FixStepAsksTheLabInstead()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)], Mapping = new ProbeRecord { Utc = T0 } });
        Assert.Equal(LabStepAction.RefreshPresets, j.Current!.Primary);
        Assert.Equal([LabStepAction.KeepAsManualSave], j.Current.Secondary);
    }

    [Fact]
    public void GameChangedSettings_GameNotInstalled_AsksTheLab()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)], CanLaunch = false });
        Assert.Equal(LabStepAction.RefreshPresets, j.Current!.Primary);
    }

    [Fact]
    public void GameChangedSettings_WithoutTheLabOrTheGame_HasNoMainButton_ButKeepStays()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)], CanLaunch = false, LabReady = false });
        Assert.Equal(LabStepAction.None, j.Current!.Primary);
        Assert.Equal([LabStepAction.KeepAsManualSave], j.Current.Secondary);
    }

    [Fact]
    public void GameChangedSettings_LabAlreadyLooking_HasNoMainButton_AndSaysSo()
    {
        var j = LabSteps.Build(In() with { Recent = [Run(0, drifted: 4), Run(1, drifted: 4)], LabRequest = Issue(LabStatus.Researching) });
        Assert.Equal(LabStepAction.None, j.Current!.Primary);
        Assert.Contains("The game lab is looking into it", j.Current.Detail);
    }

    [Fact]
    public void GameChangedSettings_FoundByATest_IsTheFixToo()
    {
        var test = Run(0, drifted: 3, verify: true);
        var j = LabSteps.Build(In() with { LastVerify = test });
        Assert.True(j.Current!.IsFix);
        Assert.Equal("Fix: the game changed 3 of 10 settings", j.Current.Title);
        Assert.Same(test, j.ChangedRun);
    }

    [Fact]
    public void ANewerHeldLaunch_ClearsTheFix()
    {
        var changed = In() with { Recent = [Overridden(0)] };
        Assert.True(LabSteps.Build(changed).Current!.IsFix);

        var j = LabSteps.Build(changed with { Recent = [Overridden(0), Run(1)] });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
        Assert.Null(j.ChangedRun);
        Assert.DoesNotContain(j.Steps, s => s.IsFix);
    }

    [Fact]
    public void ANewerHeldTest_ClearsTheFix()
    {
        var j = LabSteps.Build(In() with { Recent = [Overridden(0)], LastVerify = Run(1, verify: true) });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
    }

    [Fact]
    public void TheGameKeepsChangingThings_IsNoLongerAFix_ButAnAdviceToSetItInGame()
    {
        // two launches in a row that the game reset: the health suggests the flag, which comes before "changed some settings"
        var j = LabSteps.Build(In() with { Recent = [Overridden(0), Overridden(1)] });
        Assert.Equal(HealthState.CantBeSetFromOutside, j.Health.State);
        Assert.Equal(LabStepAction.FlagSetInGame, j.Current!.Primary);
        Assert.False(j.Current.IsFix);
    }

    [Fact]
    public void EvidenceTheGameIgnoresTheSettings_SuggestsFlaggingItInTheConfirmStep()
    {
        var j = LabSteps.Build(In() with { OverriddenStreak = 2 });
        Assert.Equal(LabJourneyKind.Steps, j.Kind);
        Assert.Equal(LabStepId.ConfirmKept, j.Current!.Id);
        Assert.Equal("This game may need to be set in-game", j.Current.Title);
        Assert.Equal(LabStepAction.FlagSetInGame, j.Current.Primary);
        Assert.Equal([LabStepAction.RefreshPresets], j.Current.Secondary);
        Assert.Equal(3, j.Count);
    }

    // ---------------------------------------------------------------- game updated

    [Fact]
    public void GameUpdated_ReopensPlayOnce_AndConfirmGoesBackToUpcoming()
    {
        var j = LabSteps.Build(Held() with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" });
        Assert.Equal(HealthState.GameUpdated, j.Health.State);
        AssertStates(j, StepState.Done, StepState.Current, StepState.Upcoming); // step 1 stays done
        Assert.Equal("Play once after the update", j.Current!.Title);
        Assert.Equal(LabStepAction.Play, j.Current.Primary);
        Assert.Equal([LabStepAction.RefreshPresets], j.Current.Secondary);
        Assert.Equal("Step 2 of 3", j.ProgressText);
        Assert.Equal("", j.Steps[2].DoneSummary);
        Assert.Equal(LabStepAction.None, j.Steps[2].Primary);
    }

    [Fact]
    public void GameUpdated_ThenAHeldLaunchOnTheNewBuild_IsAllSetAgain()
    {
        var j = LabSteps.Build(Held() with
        {
            Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2", Recent = [Run(0), Run(1), Run(2), Run(3, build: "steam:2")],
        });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
    }

    [Fact]
    public void GameUpdated_ThenALaunchOnTheNewBuildThatChangedThings_IsTheFix()
    {
        var j = LabSteps.Build(In() with
        {
            Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2", Recent = [Overridden(1, build: "steam:2")],
        });
        Assert.Equal(HealthState.GameUpdated, j.Health.State);
        AssertStates(j, StepState.Done, StepState.Done, StepState.Current);
        Assert.True(j.Current!.IsFix);
    }

    [Fact]
    public void GameUpdated_ThenAnUnclearLaunchOnTheNewBuild_AsksToPlayAgain()
    {
        var j = LabSteps.Build(In() with
        {
            Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2", Recent = [Run(1, sec: 20, build: "steam:2")],
        });
        Assert.Equal(LabStepId.ConfirmKept, j.Current!.Id);
        Assert.False(j.Current.IsFix);
        Assert.Equal(LabStepAction.Play, j.Current.Primary);
    }

    [Fact]
    public void GameUpdated_NoRefreshWhileTheLabIsLooking()
    {
        var j = LabSteps.Build(In() with
        {
            Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2", LabRequest = Issue(LabStatus.Researching),
        });
        Assert.Empty(j.Current!.Secondary);
    }

    // ---------------------------------------------------------------- step 4: settings sync (only when unsure)

    [Fact]
    public void SyncUnknown_AddsAFourthStep_AfterAllSet_ItIsCurrent()
    {
        var j = LabSteps.Build(Held() with { CloudSync = UnknownSync() });
        Assert.Equal(LabJourneyKind.Steps, j.Kind);
        AssertIds(j, LabStepId.GetPresets, LabStepId.PlayOnce, LabStepId.ConfirmKept, LabStepId.CheckSync);
        AssertStates(j, StepState.Done, StepState.Done, StepState.Done, StepState.Current);
        Assert.Equal(4, j.Count);
        Assert.Equal(3, j.CurrentIndex);
        Assert.Equal("Step 4 of 4", j.ProgressText);
        Assert.Equal("Check settings sync between devices", j.Current!.Title);
        Assert.Equal(LabStepAction.RecheckSync, j.Current.Primary);
    }

    [Fact]
    public void SyncKnown_LeavesTheStepOut()
    {
        var j = LabSteps.Build(Held() with { CloudSync = PerMachine() });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
        Assert.Equal(3, j.Count);
        Assert.DoesNotContain(j.Steps, s => s.Id == LabStepId.CheckSync);
        Assert.DoesNotContain(LabSteps.Build(Held()).Steps, s => s.Id == LabStepId.CheckSync); // no report at all: nothing to check
    }

    [Fact]
    public void SyncUnknown_WithoutAReadyLab_LeavesTheStepOut_SoNoStepCanNeverComplete()
    {
        var j = LabSteps.Build(Held() with { CloudSync = UnknownSync(), LabReady = false });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
        Assert.DoesNotContain(j.Steps, s => s.Id == LabStepId.CheckSync);
    }

    [Fact]
    public void SyncUnsure_IsTheLastStep_WithItsReason()
    {
        var j = LabSteps.Build(Held() with { CloudSync = ChangedBehindOurBack() });
        Assert.Equal(HealthState.SyncUnsure, j.Health.State);
        Assert.Equal(4, j.Count);
        Assert.Equal(LabStepId.CheckSync, j.Current!.Id);
        Assert.Equal("The settings changed while Optimus-Vibe wasn't running.", j.Current.Detail);
        Assert.Equal(LabStepAction.RecheckSync, j.Current.Primary);
        AssertStates(j, StepState.Done, StepState.Done, StepState.Done, StepState.Current);
    }

    [Fact]
    public void SyncStep_WaitsForThePlayStepsFirst()
    {
        var j = LabSteps.Build(In() with { CloudSync = UnknownSync() });
        Assert.Equal(4, j.Count);
        AssertStates(j, StepState.Done, StepState.Current, StepState.Upcoming, StepState.Upcoming);
        Assert.Equal(LabStepAction.None, j.Steps[3].Primary); // upcoming: no button yet
    }

    [Fact]
    public void SyncStep_NoButtonWhileTheLabIsWorking()
    {
        var j = LabSteps.Build(Held() with { CloudSync = UnknownSync(), LabRequest = Issue(LabStatus.Researching) });
        Assert.Equal(LabStepAction.None, j.Current!.Primary);
    }

    [Fact]
    public void AllSet_WhileTheLabStillNeedsAMappingRun_OffersMappingTheGame()
    {
        var j = LabSteps.Build(Held() with { LabRequest = Issue(LabStatus.NeedsEvidence) });
        Assert.Equal(LabJourneyKind.AllSet, j.Kind);
        Assert.Equal(LabStepAction.MapThisGame, j.Action);
        Assert.Contains("needs to know where this game keeps its settings", j.Detail);

        Assert.Equal(LabStepAction.None, LabSteps.Build(Held()).Action);
        Assert.Equal(LabStepAction.None, LabSteps.Build(Held() with { LabRequest = Issue(LabStatus.NeedsEvidence), Mapping = new ProbeRecord { Utc = T0 } }).Action);
    }

    // ---------------------------------------------------------------- set in-game

    [Fact]
    public void Flagged_IsSetInGame_WithNoSteps()
    {
        var flag = GameFlags.Create("the game rewrites its settings on every boot", issue: 7, today: T0);
        var j = LabSteps.Build(In() with { Flag = flag, HasUserFlag = true, Recent = [Overridden(0)] });
        Assert.Equal(LabJourneyKind.SetInGame, j.Kind);
        Assert.Empty(j.Steps);
        Assert.Equal(0, j.Count);
        Assert.Equal(-1, j.CurrentIndex);
        Assert.Equal(GameFlags.StatusLine(flag), j.Headline);
        Assert.Equal("Set in-game", j.ProgressText);
        Assert.Equal(LabStepAction.None, j.Action); // the lab is not done looking yet
    }

    [Fact]
    public void Flagged_ResearchFinished_OffersManagingSettingsAgain()
    {
        var flag = GameFlags.Create("the game rewrites its settings on every boot", issue: 7, today: T0);
        var j = LabSteps.Build(In() with { Flag = flag, HasUserFlag = true, ResearchFinished = true });
        Assert.Equal(LabStepAction.ManageAgain, j.Action);
        Assert.Equal(GameHealth.ResearchFinishedLine, j.Detail);
    }

    [Fact]
    public void Flagged_ByTheGameConfig_OnlyTheLabCanLiftIt()
    {
        var flag = GameFlags.Create("the game rewrites its settings on every boot", issue: 7, today: T0);
        var j = LabSteps.Build(In() with { Flag = flag, FlagFromConfig = true, ResearchFinished = true });
        Assert.Equal(LabStepAction.None, j.Action);
        Assert.Equal(GameHealth.ConfigFlagLine, j.Detail);
    }

    [Fact]
    public void Flagged_WinsOverMissingPresets()
    {
        var flag = GameFlags.Create("the game rewrites its settings on every boot", issue: 7, today: T0);
        Assert.Equal(LabJourneyKind.SetInGame, LabSteps.Build(In() with { HasPresets = false, Flag = flag, HasUserFlag = true }).Kind);
    }

    // ---------------------------------------------------------------- invariants over every situation

    public static IEnumerable<object[]> Situations()
    {
        var flag = GameFlags.Create("the game rewrites its settings", issue: 7, today: T0);
        yield return ["no presets", In() with { HasConfig = false, HasPresets = false }];
        yield return ["lab working", In() with { HasPresets = false, LabRequest = Issue(LabStatus.NeedsEvidence) }];
        yield return ["never played", In()];
        yield return ["played, held", In() with { Recent = [Run(0)] }];
        yield return ["working", Held()];
        yield return ["unclear", In() with { Recent = [Run(0, sec: 20), Run(1, sec: 20), Run(2, sec: 20)] }];
        yield return ["changed", In() with { Recent = [Overridden(0)] }];
        yield return ["changed by a test", In() with { LastVerify = Run(0, drifted: 3, verify: true) }];
        yield return ["cannot be set", In() with { OverriddenStreak = 2 }];
        yield return ["updated", Held() with { Presets = new PresetsMark { MadeUtc = T0, BuildId = "steam:1" }, CurrentBuild = "steam:2" }];
        yield return ["sync unknown", Held() with { CloudSync = UnknownSync() }];
        yield return ["sync unsure", Held() with { CloudSync = ChangedBehindOurBack() }];
        yield return ["sync unknown, never played", In() with { CloudSync = UnknownSync() }];
        yield return ["flagged", In() with { Flag = flag, HasUserFlag = true }];
        yield return ["not installed", Held() with { CanLaunch = false, Recent = [] }];
    }

    [Theory]
    [MemberData(nameof(Situations))]
    public void EveryJourney_IsConsistent(string name, HealthInputs inputs)
    {
        var j = LabSteps.Build(inputs);
        switch (j.Kind)
        {
            case LabJourneyKind.Steps:
                Assert.True(j.CurrentIndex >= 0 && j.CurrentIndex < j.Count, name);
                Assert.Equal(1, j.Steps.Count(s => s.State == StepState.Current));
                // done before the current one, upcoming after it
                for (var k = 0; k < j.Count; k++)
                    Assert.Equal(k < j.CurrentIndex ? StepState.Done : k == j.CurrentIndex ? StepState.Current : StepState.Upcoming, j.Steps[k].State);
                Assert.Equal($"Step {j.CurrentIndex + 1} of {j.Count}", j.ProgressText);
                break;
            case LabJourneyKind.AllSet:
                Assert.All(j.Steps, s => Assert.Equal(StepState.Done, s.State));
                Assert.Equal(LabSteps.AllSetHeadline, j.Headline);
                break;
            case LabJourneyKind.SetInGame:
                Assert.Empty(j.Steps);
                break;
        }
        // steps are in catalog order, each at most once
        Assert.Equal(j.Steps.Select(s => s.Id).Distinct().OrderBy(x => x), j.Steps.Select(s => s.Id));
        foreach (var s in j.Steps)
        {
            Assert.NotEmpty(s.Title);
            Assert.NotEmpty(s.Detail);
            Assert.DoesNotContain('\n', s.Detail);
            if (s.State == StepState.Done) Assert.NotEqual("", s.DoneSummary);
            else Assert.Equal("", s.DoneSummary);
            if (s.State != StepState.Current)
            {
                Assert.Equal(LabStepAction.None, s.Primary);
                Assert.Empty(s.Secondary);
            }
            Assert.DoesNotContain(s.Secondary, a => a == s.Primary || a == LabStepAction.None);
        }
    }

    [Theory]
    [InlineData(false, false, false, 3)] // presets, nothing else
    [InlineData(true, false, false, 4)] // + sync unknown
    [InlineData(false, true, false, 3)] // sync known: left out
    public void Count_IsDynamic(bool syncUnknown, bool syncKnown, bool noPresets, int expected)
    {
        var i = In() with { HasPresets = !noPresets, CloudSync = syncUnknown ? UnknownSync() : syncKnown ? PerMachine() : null };
        Assert.Equal(expected, LabSteps.Build(i).Count);
    }
}
