using System.Xml.Linq;

namespace OptimusVibe.Tests;

/// <summary>
/// The game page's lab panel (<c>Views/LabGamePanel.xaml</c>) after it was reorganised around the health status line: no action was removed
/// or duplicated. Every command the panel bound before is still bound, the actions under "More" are there exactly once (shared by both
/// layouts), and the controller rules hold (More is a contained focus scope, the title row keeps one Open issue per layout).
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

    private static List<XElement> Bound(XContainer root, string command) =>
        root.Descendants().Where(e => (string?)e.Attribute("Command") == $"{{Binding {command}}}").ToList();

    private static XElement More(XDocument doc) =>
        doc.Descendants(Wpf + "StackPanel").Single(e => (string?)e.Attribute(X + "Name") == "MorePanel");

    [Theory]
    // the panel's actions: once each, under More (or in the suggested slot through SuggestedCommand)
    [InlineData("RequestCommand", 1)]
    [InlineData("RefreshPresetsCommand", 1)]
    [InlineData("SyncCheckCommand", 1)]
    [InlineData("TestCommand", 1)]
    [InlineData("MapCommand", 1)]
    [InlineData("ManageAgainCommand", 1)]
    [InlineData("SuggestedCommand", 1)]
    [InlineData("ToggleMoreCommand", 1)]
    // under More, and the test result card's own button
    [InlineData("FlagCommand", 2)]
    // one per layout (title row)
    [InlineData("OpenIssueCommand", 2)]
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
    public void EveryCommand_IsBound_AsOftenAsBefore(string command, int times)
    {
        Assert.Equal(times, Bound(Panel(), command).Count);
    }

    [Theory]
    [InlineData("RequestCommand", "ShowRequestInMore", "RequestCaption")]
    [InlineData("RefreshPresetsCommand", "ShowRefreshInMore", "RefreshCaption")]
    [InlineData("SyncCheckCommand", "ShowSyncCheckInMore", "SyncCheckCaption")]
    [InlineData("TestCommand", "ShowTestInMore", "TestCaption")]
    [InlineData("MapCommand", "ShowMapInMore", "MapCaption")]
    [InlineData("ManageAgainCommand", "ShowManageAgainInMore", "ManageAgainCaption")]
    [InlineData("FlagCommand", "ShowFlagInMore", "FlagCaption")]
    public void EveryAction_IsUnderMore_WithItsCaption(string command, string visibleWhen, string caption)
    {
        var button = Assert.Single(Bound(More(Panel()), command));
        var row = button.Parent!;
        Assert.Contains(visibleWhen, (string?)row.Attribute("Visibility"));
        Assert.Contains(row.Elements(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == $"{{Binding {caption}}}");
    }

    [Fact]
    public void More_IsAContainedFocusScope_ShownOnlyWhenOpen()
    {
        var more = More(Panel());
        Assert.Equal("Contained", (string?)more.Attribute("KeyboardNavigation.DirectionalNavigation"));
        Assert.Contains("IsMoreOpen", (string?)more.Attribute("Visibility"));
    }

    [Fact]
    public void TheStatusLine_AndWhyLine_AreReadOnlyText_BeforeTheSuggestedAction()
    {
        var doc = Panel();
        var all = doc.Descendants().ToList();
        var status = all.FindIndex(e => (string?)e.Attribute("Text") == "{Binding HealthText}");
        var why = all.FindIndex(e => (string?)e.Attribute("Text") == "{Binding HealthWhy}");
        var suggested = all.FindIndex(e => (string?)e.Attribute("Command") == "{Binding SuggestedCommand}");
        var toggle = all.FindIndex(e => (string?)e.Attribute("Command") == "{Binding ToggleMoreCommand}");
        Assert.True(status >= 0 && why > status && suggested > why && toggle > suggested, "focus order: status, why, suggested, More");
        Assert.Equal(Wpf + "TextBlock", all[status].Name);
        Assert.Equal(Wpf + "TextBlock", all[why].Name);
    }

    [Fact]
    public void OpenIssue_IsOncePerLayout()
    {
        var layouts = Bound(Panel(), "OpenIssueCommand")
            .Select(b => b.Ancestors().Select(a => (string?)a.Attribute("Style")).FirstOrDefault(s => s?.Contains("Only.") == true))
            .ToList();
        Assert.Equal(["{StaticResource Only.Comfortable}", "{StaticResource Only.Compact}"], layouts);
    }

    [Fact]
    public void TheOldButtonRowsAndSuggestionBlock_AreGone()
    {
        var doc = Panel();
        // the hint moved into the status line, the separate set-in-game and suggestion blocks into the why line, Suggested and More
        Assert.DoesNotContain(doc.Descendants(), e => ((string?)e.Attribute("Text"))?.Contains("HintText") == true);
        Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("Visibility") == "{Binding ShowSuggest, Converter={StaticResource VisibleWhen}}");
        Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("Visibility") == "{Binding IsFlagged, Converter={StaticResource VisibleWhen}}");
    }
}
