using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

/// <summary>The post-game prompt's decision plumbing, with a fake prompt in place of the window.</summary>
public sealed class CapturePromptTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private const string G = EngineFixture.GameId;

    public CapturePromptTests()
    {
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\nTextures=3\r\n");
        _f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(_f.GameFile("blob.bin"), [1]);
    }

    public void Dispose() => _f.Dispose();

    private sealed class FakePrompt(CaptureDecision decision) : ICapturePrompt
    {
        public List<CaptureRequest> Asked { get; } = new();

        public CaptureDecision Ask(CaptureRequest request)
        {
            Asked.Add(request);
            return decision;
        }
    }

    /// <summary>Applies the performance preset, then changes Textures in the game as a player would.</summary>
    private (GameContext Ctx, AppliedState Applied) PlayAndTweak(PresetRef? preset = null)
    {
        var ctx = _f.Load("desktop", preset ?? PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        _f.WriteGameFile("user.ini", "[S]\r\nShadows=1.000000\r\nTextures=4\r\n");
        return (ctx, applied);
    }

    [Fact]
    public void Save_CreatesANewManualSave_AndMakesItActive()
    {
        var (ctx, applied) = PlayAndTweak();
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.Save));

        var saved = Runner.OfferCapture(ctx, applied, prompt);

        Assert.NotNull(saved);
        Assert.StartsWith("Tuned ", saved!.Save.Name);
        var request = Assert.Single(prompt.Asked);
        Assert.Equal(G, request.GameId);
        Assert.Equal(1, request.TotalChanges);
        Assert.Equal("[S]Textures: 3 → 4", Assert.Single(request.Lines));
        Assert.Null(request.ActiveSaveName);
        Assert.Equal(PresetRef.Manual(saved.Save.Slug), _f.Selections.Get("desktop", G));
    }

    [Fact]
    public void Save_WhileAManualSaveIsActive_UpdatesThatSave()
    {
        var original = ManualSaves.SaveCurrent(_f.Load("desktop"), "Steady", sync: false);
        var (ctx, applied) = PlayAndTweak(PresetRef.Manual(original.Save.Slug));
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.Save));

        var saved = Runner.OfferCapture(ctx, applied, prompt);

        Assert.Equal(original.Save.Slug, saved!.Save.Slug);
        Assert.Equal("Steady", Assert.Single(prompt.Asked).ActiveSaveName);
        Assert.Single(_f.Store.ListSaves(G, "desktop"));
    }

    [Fact]
    public void SaveAs_UsesTheTypedName_AndNeverUpdatesTheActiveSave()
    {
        var original = ManualSaves.SaveCurrent(_f.Load("desktop"), "Steady", sync: false);
        var (ctx, applied) = PlayAndTweak(PresetRef.Manual(original.Save.Slug));

        var saved = Runner.OfferCapture(ctx, applied, new FakePrompt(new CaptureDecision(CaptureChoice.SaveAs, "  Couch mode ")));

        Assert.Equal("Couch mode", saved!.Save.Name);
        Assert.NotEqual(original.Save.Slug, saved.Save.Slug);
        Assert.Equal(2, _f.Store.ListSaves(G, "desktop").Count);
    }

    [Fact]
    public void SaveAs_WithABlankName_FallsBackToTheSuggestion()
    {
        var (ctx, applied) = PlayAndTweak();
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.SaveAs, "   "));

        var saved = Runner.OfferCapture(ctx, applied, prompt);

        Assert.Equal(prompt.Asked[0].SuggestedName, saved!.Save.Name);
    }

    [Fact]
    public void DontSave_WritesNothing_AndAsksAgainNextTime()
    {
        var (ctx, applied) = PlayAndTweak();
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.DontSave));

        Assert.Null(Runner.OfferCapture(ctx, applied, prompt));
        Assert.Null(Runner.OfferCapture(ctx, applied, prompt));

        Assert.Empty(_f.Store.ListSaves(G, "desktop"));
        Assert.Equal(2, prompt.Asked.Count);
        Assert.False(CapturePromptStore.IsSkipped(_f.Paths.DataDir, G));
    }

    [Fact]
    public void DontAskAgain_PersistsPerGame_AndSuppressesThePrompt()
    {
        var (ctx, applied) = PlayAndTweak();
        var first = new FakePrompt(new CaptureDecision(CaptureChoice.DontAskAgain));

        Assert.Null(Runner.OfferCapture(ctx, applied, first));
        Assert.Empty(_f.Store.ListSaves(G, "desktop"));
        Assert.True(CapturePromptStore.IsSkipped(_f.Paths.DataDir, G));
        Assert.True(CapturePromptStore.IsSkipped(_f.Paths.DataDir, G.ToUpperInvariant()));
        Assert.False(CapturePromptStore.IsSkipped(_f.Paths.DataDir, "other-game"));

        var second = new FakePrompt(new CaptureDecision(CaptureChoice.Save));
        Assert.Null(Runner.OfferCapture(ctx, applied, second));
        Assert.Empty(second.Asked);
        Assert.Empty(_f.Store.ListSaves(G, "desktop"));
    }

    [Fact]
    public void ReEnabling_AsksAgain()
    {
        var (ctx, applied) = PlayAndTweak();
        CapturePromptStore.SetSkipped(_f.Paths.DataDir, G, true);
        Assert.Contains(G, CapturePromptStore.Skipped(_f.Paths.DataDir));
        CapturePromptStore.SetSkipped(_f.Paths.DataDir, G, false);

        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.DontSave));
        Runner.OfferCapture(ctx, applied, prompt);

        Assert.Single(prompt.Asked);
        Assert.Empty(CapturePromptStore.Skipped(_f.Paths.DataDir));
    }

    [Fact]
    public void NoDrift_NeverAsks()
    {
        var ctx = _f.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.Save));

        Assert.Null(Runner.OfferCapture(ctx, applied, prompt));
        Assert.Empty(prompt.Asked);
    }

    [Fact]
    public void ManyChanges_ListAtMostTwelveLines_AndCountTheRest()
    {
        var ini = string.Concat(Enumerable.Range(1, 15).Select(i => $"K{i}=1\r\n"));
        using var f = new EngineFixture(EngineFixture.GameYaml.Replace("user: { S: { Shadows: 1 } }", "user: { S: { Shadows: 1" +
            string.Concat(Enumerable.Range(1, 15).Select(i => $", K{i}: 1")) + " } }"));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=9\r\n" + ini);
        f.WriteGameFile("engine.ini", "[E]\r\nLod=1\r\n");
        File.WriteAllBytes(f.GameFile("blob.bin"), [1]);
        var ctx = f.Load("desktop", PresetRef.Recommended("performance"));
        var applied = GameEngine.Apply(ctx, GameEngine.Plan(ctx));
        f.WriteGameFile("user.ini", "[S]\r\nShadows=1\r\nTextures=3\r\n" + string.Concat(Enumerable.Range(1, 15).Select(i => $"K{i}=2\r\n")));
        var prompt = new FakePrompt(new CaptureDecision(CaptureChoice.DontSave));

        Runner.OfferCapture(ctx, applied, prompt);

        var r = Assert.Single(prompt.Asked);
        Assert.Equal(15, r.TotalChanges);
        Assert.Equal(CapturePrompts.MaxLines, r.Lines.Count);
        Assert.Equal(3, r.More);
    }

    [Fact]
    public void ThrowingPrompt_IsSwallowed()
    {
        var (ctx, applied) = PlayAndTweak();
        Assert.Null(Runner.OfferCapture(ctx, applied, new ThrowingPrompt()));
        Assert.Empty(_f.Store.ListSaves(G, "desktop"));
    }

    private sealed class ThrowingPrompt : ICapturePrompt
    {
        public CaptureDecision Ask(CaptureRequest request) => throw new InvalidOperationException("no window");
    }

    [Fact]
    public void Store_SurvivesACorruptFile()
    {
        File.WriteAllText(CapturePromptStore.FileFor(_f.Paths.DataDir), "{ not json");
        Assert.False(CapturePromptStore.IsSkipped(_f.Paths.DataDir, G));
        CapturePromptStore.SetSkipped(_f.Paths.DataDir, G, true);
        Assert.True(CapturePromptStore.IsSkipped(_f.Paths.DataDir, G));
    }
}
