using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OptimusVibe.Tests;

/// <summary>
/// The game page's lab panel (<c>Views/LabGamePanel.xaml</c>) after it was reorganised into the guided "Step X of N" checklist: no action was
/// removed or duplicated. Every command the panel bound before is still bound (the step buttons through the view model's rows, the rest where
/// they were), the actions under "Manual tools" are there exactly once, and the controller rules hold (Manual tools is a contained focus scope,
/// the header keeps one Open issue, the checklist comes before the cards and Manual tools sits last).
/// </summary>
public sealed class LabPanelLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Panel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OptimusVibe.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir!.FullName, "src", "OptimusVibe.App", "Views", "LabGamePanel.xaml"));
    }

    /// <summary>A command bound directly (<c>{Binding X}</c>) or through the panel's data context from inside a row template (<c>{Binding DataContext.X, ...}</c>).</summary>
    private static bool IsBoundTo(XElement e, string command) =>
        e.Attribute("Command")?.Value is { } v && Regex.IsMatch(v, $@"^\{{Binding (DataContext\.)?{command}(,.*)?\}}$");

    private static List<XElement> Bound(XContainer root, string command) => root.Descendants().Where(e => IsBoundTo(e, command)).ToList();

    private static XElement Tools(XDocument doc) =>
        doc.Descendants(Wpf + "StackPanel").Single(e => (string?)e.Attribute(X + "Name") == "ToolsPanel");

    private static XElement Resource(XDocument doc, string key) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key);

    [Theory]
    // the panel's actions: once each under Manual tools (the step buttons are the view model's rows, bound through StepButton.Command)
    [InlineData("RequestCommand", 1)]
    [InlineData("RefreshPresetsCommand", 1)]
    [InlineData("SyncCheckCommand", 1)]
    [InlineData("TestCommand", 1)]
    [InlineData("MapCommand", 1)]
    [InlineData("ManageAgainCommand", 1)]
    [InlineData("VerifyNextCommand", 1)]
    [InlineData("UnmuteCommand", 1)]
    [InlineData("ToggleToolsCommand", 1)]
    // the fix step's "See which"
    [InlineData("ToggleChangedCommand", 1)]
    // under Manual tools, and the test result card's own button
    [InlineData("FlagCommand", 2)]
    // the header
    [InlineData("OpenIssueCommand", 1)]
    // the cards, untouched
    [InlineData("OpenSettingsCommand", 1)]
    [InlineData("ConfirmFlagCommand", 1)]
    [InlineData("ForgetAutoCommand", 1)]
    [InlineData("ConfirmForgetCommand", 1)]
    [InlineData("StartTestCommand", 1)]
    [InlineData("SendResultCommand", 1)]
    [InlineData("SaveResultCommand", 1)]
    [InlineData("DismissResultCommand", 1)]
    [InlineData("ConfirmCommand", 1)]
    [InlineData("MapFirstCommand", 1)]
    // "Not now" and "Don't ask again": on the fix step and on the card that stayed a card (they run through the view model, never a second bound copy)
    [InlineData("PostDismissCommand", 2)]
    [InlineData("PostMuteCommand", 2)]
    // the card that stayed a card
    [InlineData("PostPrimaryCommand", 1)]
    [InlineData("PostSecondaryCommand", 1)]
    public void EveryCommand_IsBound_AsOftenAsExpected(string command, int times)
    {
        Assert.Equal(times, Bound(Panel(), command).Count);
    }

    [Theory]
    [InlineData("RequestCommand", "ShowRequestInTools", "RequestCaption")]
    [InlineData("RefreshPresetsCommand", "ShowRefreshInTools", "RefreshCaption")]
    [InlineData("SyncCheckCommand", "ShowSyncCheckInTools", "SyncCheckCaption")]
    [InlineData("TestCommand", "ShowTestInTools", "TestCaption")]
    [InlineData("MapCommand", "ShowMapInTools", "MapCaption")]
    [InlineData("ManageAgainCommand", "ShowManageAgainInTools", "ManageAgainCaption")]
    [InlineData("FlagCommand", "ShowFlagInTools", "FlagCaption")]
    [InlineData("VerifyNextCommand", "ShowVerifyNextInTools", "VerifyNextCaption")]
    [InlineData("UnmuteCommand", "ShowUnmuteInTools", "UnmuteCaption")]
    public void EveryAction_IsUnderManualTools_WithItsCaption(string command, string visibleWhen, string caption)
    {
        var button = Assert.Single(Bound(Tools(Panel()), command));
        var row = button.Parent!;
        Assert.Contains(visibleWhen, (string?)row.Attribute("Visibility"));
        Assert.Contains(row.Elements(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == $"{{Binding {caption}}}");
    }

    [Fact]
    public void ManualTools_KeepTheirButtonTexts()
    {
        var buttons = Tools(Panel()).Descendants(Wpf + "Button").ToList();
        var texts = buttons.Select(b => (string?)b.Attribute("Content") ?? "").ToList();
        foreach (var text in new[]
                 {
                     "Refresh presets", "Re-check settings sync", "Test preset", "Map this game", "Manage settings again", "Flag as set in-game",
                     "Show cards after sessions again", "{Binding RequestText}", "{Binding VerifyNextText}",
                 })
            Assert.Contains(text, texts);
    }

    [Fact]
    public void ManualTools_IsAContainedFocusScope_ShownOnlyWhenOpen_AtTheBottomRight()
    {
        var doc = Panel();
        var tools = Tools(doc);
        Assert.Equal("Contained", (string?)tools.Attribute("KeyboardNavigation.DirectionalNavigation"));
        Assert.Contains("IsToolsOpen", (string?)tools.Attribute("Visibility"));

        var toggle = Assert.Single(Bound(doc, "ToggleToolsCommand"));
        Assert.Equal("Right", (string?)toggle.Attribute("HorizontalAlignment"));
        Assert.Equal("Manual tools", (string?)toggle.Attribute("AutomationProperties.Name"));

        // last in the panel: after the checklist and the cards, its list right under its button
        var all = doc.Descendants().ToList();
        Assert.True(all.IndexOf(toggle) > all.FindIndex(e => (string?)e.Attribute(X + "Name") == "StepsHost"));
        Assert.True(all.IndexOf(toggle) > all.FindLastIndex(e => (string?)e.Attribute("Command") == "{Binding ConfirmCommand}"));
        Assert.True(all.IndexOf(tools) > all.IndexOf(toggle));
        Assert.Same(toggle.NextNode, tools);
        Assert.Null(tools.NextNode);
    }

    [Fact]
    public void TheChecklist_ComesFirst_ThenTheCardThatStayedACard()
    {
        var doc = Panel();
        var all = doc.Descendants().ToList();
        var header = all.FindIndex(e => (string?)e.Attribute("Text") == "{Binding ProgressText}");
        var bar = all.FindIndex(e => e.Name == Wpf + "ItemsControl" && (string?)e.Attribute("ItemTemplate") == "{StaticResource StepSegment}");
        var steps = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "StepsHost");
        var summary = all.FindIndex(e => (string?)e.Attribute("Text") == "{Binding SummaryHeadline}");
        var card = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "PostCard");
        Assert.True(header >= 0 && bar > header && steps > bar && summary > steps && card > summary, "order: Step X of N, progress, steps, summary, card");
    }

    [Fact]
    public void TheHeader_SaysGameLab_AndStepXOfN()
    {
        var doc = Panel();
        Assert.Contains(doc.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "Game lab");
        Assert.Contains(doc.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding ProgressText}");
    }

    [Fact]
    public void TheStepButtons_AreFocusableButtons_TheMainOneWithTheAGlyph_ACancelWithB()
    {
        var doc = Panel();
        var primary = Resource(doc, "StepPrimaryButton").Descendants(Wpf + "Button").Single();
        Assert.Equal("{StaticResource Button.Primary}", (string?)primary.Attribute("Style"));
        Assert.Equal("{Binding Command}", (string?)primary.Attribute("Command"));
        Assert.NotEqual("False", (string?)primary.Attribute("Focusable"));
        Assert.Contains(primary.Descendants(), e => e.Name.LocalName == "ButtonGlyph" && (string?)e.Attribute("Kind") == "A");

        var quiet = Resource(doc, "StepQuietButton").Descendants(Wpf + "Button").Single();
        Assert.Equal("{StaticResource Button.Quiet}", (string?)quiet.Attribute("Style"));
        Assert.Equal("{Binding Command}", (string?)quiet.Attribute("Command"));
        Assert.Contains(quiet.Descendants(), e => e.Name.LocalName == "ButtonGlyph" && (string?)e.Attribute("Kind") == "B");

        // the template hosts are not tab stops of their own: focus lands on the buttons
        var row = Resource(doc, "StepRowTemplate");
        foreach (var host in row.Descendants(Wpf + "ContentControl").Concat(row.Descendants(Wpf + "ItemsControl")))
            Assert.Equal("False", (string?)host.Attribute("Focusable"));
    }

    [Fact]
    public void TheFixStep_OffersSeeWhich_AndNotNow_AndDontAskAgain_WithBForNotNow()
    {
        var row = Resource(Panel(), "StepRowTemplate");
        Assert.Single(Bound(row, "ToggleChangedCommand"));
        var notNow = Assert.Single(Bound(row, "PostDismissCommand"));
        Assert.Contains(notNow.Descendants(), e => e.Name.LocalName == "ButtonGlyph" && (string?)e.Attribute("Kind") == "B");
        Assert.Single(Bound(row, "PostMuteCommand"));
    }

    [Fact]
    public void ThePostSessionCard_StaysInline_Contained_WithItsActionsAndBForNotNow()
    {
        var doc = Panel();
        var card = doc.Descendants(Wpf + "Border").Single(e => (string?)e.Attribute(X + "Name") == "PostCard");
        Assert.Equal("{StaticResource Card.Inset}", (string?)card.Attribute("Style"));
        Assert.Equal("Contained", (string?)card.Attribute("KeyboardNavigation.DirectionalNavigation"));
        Assert.Contains("HasPostSession", (string?)card.Attribute("Visibility"));
        Assert.Single(Bound(card, "PostSecondaryCommand"));
        var primary = Assert.Single(Bound(card, "PostPrimaryCommand"));
        Assert.Contains(primary.Descendants(), e => e.Name.LocalName == "ButtonGlyph" && (string?)e.Attribute("Kind") == "A");
        var dismiss = Assert.Single(Bound(card, "PostDismissCommand"));
        Assert.Contains(dismiss.Descendants(), e => e.Name.LocalName == "ButtonGlyph" && (string?)e.Attribute("Kind") == "B");
        // no existing game-page command is bound in it a second time: its actions go through the view model to the existing commands
        Assert.All(card.Descendants().Select(e => (string?)e.Attribute("Command")).OfType<string>(), c => Assert.StartsWith("{Binding Post", c));
    }

    [Fact]
    public void TheOldStatusLineSuggestedActionAndMore_AreGone()
    {
        var doc = Panel();
        var text = doc.ToString();
        foreach (var gone in new[] { "HealthText", "HealthWhy", "SuggestedCommand", "SuggestedText", "ToggleMoreCommand", "IsMoreOpen", "MorePanel", "MoreButton", "MoreText" })
            Assert.DoesNotContain(gone, text);
        Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("Style") is "{StaticResource Only.Comfortable}" or "{StaticResource Only.Compact}");
    }
}
