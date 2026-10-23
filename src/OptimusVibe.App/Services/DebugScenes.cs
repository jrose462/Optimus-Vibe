#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OptimusVibe.App.ViewModels;
using OptimusVibe.App.Views;
using OptimusVibe.GameLab;
using OptimusVibe.Launch;

namespace OptimusVibe.App.Services;

/// <summary>
/// Debug builds only: put the app in a known state and save a screenshot, for checking the UI without clicking.
/// OPTIMUSVIBE_SCENE = settings-lab | game-request, OPTIMUSVIBE_SHOT = output png. `OptimusVibe.exe capture-demo` shows the post-game prompt.
/// </summary>
public static class DebugScenes
{
    public static string? Scene => Environment.GetEnvironmentVariable("OPTIMUSVIBE_SCENE");
    public static string? ShotPath => Environment.GetEnvironmentVariable("OPTIMUSVIBE_SHOT");

    public static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var w = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        var bmp = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static CaptureRequest SampleCapture() => new("demo", "Hades II", "desktop", "Quality",
        ["Shadows: High → Medium", "Texture Quality: Ultra → High", "Anti-Aliasing: TAA → Off", "View Distance: 90 → 70", "Motion Blur: On → Off",
         "Ambient Occlusion: HBAO+ → SSAO", "V-Sync: Off → On", "Frame Rate Limit: 0 → 120"],
        11, null, "Tuned Oct 17 21:40");

    /// <summary>`capture-demo`: the post-game prompt with sample data; with OPTIMUSVIBE_SHOT it saves a picture and closes by itself.</summary>
    public static int RunCaptureDemo(Application app)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var saved = SettingsStore.Load();
        ThemeManager.Apply(saved.Theme);
        Density.SetMode(saved.Density);
        var w = new CapturePromptWindow(SampleCapture());
        if (ShotPath is { } shot)
            w.ContentRendered += async (_, _) =>
            {
                await Task.Delay(1500);
                Save((FrameworkElement)w.Content, shot);
                w.Close();
            };
        w.ShowDialog();
        OptimusVibe.Logging.Log.Info($"capture-demo decision: {w.Decision}");
        return 0;
    }

    /// <summary>`notes-demo [file.md]`: the pop-out release notes (sample or the given Markdown file); with OPTIMUSVIBE_SHOT it saves a picture and closes.</summary>
    public static void RunNotesDemo(Application app, string? file)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var saved = SettingsStore.Load();
        ThemeManager.Apply(saved.Theme);
        Density.SetMode(saved.Density);
        var md = file is not null && File.Exists(file) ? File.ReadAllText(file)
            : "## What's new in v0.0.0\n\n### Features\n\n- **app:** something new (#1) ([`abc1234`](https://github.com/o/r/commit/abc1234))\n  - nested detail\n\nPlain *text* with `code`.\n\n---\n**Full Changelog**: https://github.com/o/r/compare/v0.0.0...v0.0.1";
        var w = ReleaseNotesWindow.Show("What's new in 0.0.0", md, null);
        w.Closed += (_, _) => app.Shutdown(0);
        if (ShotPath is { } shot)
            w.ContentRendered += async (_, _) =>
            {
                await Task.Delay(1500);
                Save((FrameworkElement)w.Content, shot);
                w.Close();
            };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    /// <summary>OPTIMUSVIBE_WINDOW=1280x720: a normal (not maximized) window of that size in DIPs, to check the compact layout on a big screen.</summary>
    public static void ApplyWindowOverride(Window window)
    {
        var spec = Environment.GetEnvironmentVariable("OPTIMUSVIBE_WINDOW");
        if (spec is null) return;
        var parts = spec.Split('x', 'X');
        if (parts.Length != 2 || !double.TryParse(parts[0], out var w) || !double.TryParse(parts[1], out var h)) return;
        window.WindowState = WindowState.Normal;
        window.Width = w;
        window.Height = h;
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 40;
        window.Top = 20;
    }

    private sealed class DemoMapActions : OptimusVibe.App.ViewModels.IMapGameActions
    {
        public bool CanSend => true;
        public bool CanKeep => true;
        public Task<OptimusVibe.GameLab.ProbeDiff?> RunPassAsync(int pass) => Task.FromResult<OptimusVibe.GameLab.ProbeDiff?>(null);
        public OptimusVibe.GameLab.ProbeEvidence BuildEvidence() => SampleMapEvidence(true);
        public Task<string?> SendAsync() => Task.FromResult<string?>("Sent");
        public Task<string?> KeepAsync() => Task.FromResult<string?>("Kept");
        public void GameStarting() { }
        public void GameExited() { }
    }

    private static OptimusVibe.GameLab.ProbeEvidence SampleMapEvidence(bool settings) => new("", "", "",
        ["GameUserSettings.ini: 2 settings changed", "Saved\\profile.sav: changed (binary)", "3 registry value(s) rewritten"],
        settings
            ? ["GameUserSettings.ini: 9 settings changed", "graphics.sav: changed (binary)", "2 registry value(s) changed"]
            : [],
        settings ? ["{LocalAppData}\\Demo\\Saved\\graphics.sav"] : [], []);

    /// <summary>The "Map this game" window with sample data: intro | launch2 | passive (one launch left, pass A from normal launches) | summary | summary-kept | summary-none | sent | failed.</summary>
    private static async Task ShowMapSceneAsync(string phase, string shot)
    {
        var model = new OptimusVibe.App.ViewModels.MapGameModel("Hades II", new DemoMapActions());
        var passA = new OptimusVibe.GameLab.ProbeDiff(
            [new OptimusVibe.GameLab.FileChange("{LocalAppData}\\Demo\\Saved\\profile.sav", OptimusVibe.GameLab.ProbeChangeKind.Changed, 100, 120, false, "aa", "bb", "", [], 0)],
            [], []);
        switch (phase)
        {
            case "launch2": model.ShowDemo("intro2", passA); break;
            case "passive": model.ShowDemo("intro2", passA, passiveLaunches: 3); break;
            case "summary": model.ShowDemo("summary", passA, SampleMapEvidence(true)); break;
            case "summary-kept": model.ShowDemo("summary", passA, SampleMapEvidence(true), kept: true); break;
            case "summary-none": model.ShowDemo("summary", passA, SampleMapEvidence(false)); break;
            case "sent": model.ShowDemo("sent", passA, SampleMapEvidence(true)); break;
            case "failed": model.ShowDemo("failed"); break;
            default: model.ShowDemo("intro1"); break;
        }
        var w = new MapGameWindow(model);
        ApplyWindowOverride(w);
        w.Show();
        if (Environment.GetEnvironmentVariable("OPTIMUSVIBE_PAD") == "1")
            OptimusVibe.App.Input.PadState.SetIsConnected(w, true);
        await Task.Delay(1500);
        Save((FrameworkElement)w.Content, shot);
        w.Close();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Runs the named scene against the main window once it is ready, saves the screenshot and exits. Scenes: signin-rejected (the GitHub sign-in banner), settings-lab, settings-lab-auto (its
    /// "Automatic" toggles), library-chips (tile health chips, sample health), game-request,
    /// map-first (the request card offering "Map this game first"), map-intro | map-launch2 | map-summary | map-summary-kept | map-summary-none |
    /// map-sent | map-failed | map-passive (the full-screen "Map this game" window, sample data; the lab panel's Map this game button shows in every verify-* scene),
    /// library, library-focus, detail, detail-saves, detail-long-preset (long preset name in the summary row), detail-intent-focus | detail-intent-edit (the intent card focused / being edited), manage, settings, settings-appearance, report, steam-review,
    /// verify-confirm | verify-held | verify-overridden | verify-sent | verify-unknown | verify-hint  | verify-auto | verify-auto-first | verify-auto-forget| verify-status (the Test preset cards and the apply-status line, with sample data);
    /// verify-suggest | verify-suggest-map | verify-suggest-result | verify-flag-confirm | verify-flagged | verify-flagged-done | verify-flagged-config (the "set in-game" flag: suggestion, confirm card, flagged page, research finished, flag from the game config only).
    /// verify-health-working | verify-health-unchecked | verify-health-changed | verify-health-updated | verify-health-sync | verify-health-nopresets |
    /// verify-health-labworking | verify-health-more (the health states of the earlier panel, now shown as the checklist; "more" opens Manual tools),
    /// verify-health-ambiguous | verify-health-verifynext (check presets on my next launch: suggested, then on) | verify-health-changed-which
    /// (See which, open) | verify-health-evidence (presets, the lab needs evidence) | verify-health-muted (Manual tools with "Show cards after sessions again").
    /// verify-steps-nopresets | verify-steps-labworking | verify-steps-play | verify-steps-fix | verify-steps-fix-mapped | verify-steps-sync |
    /// verify-steps-allset | verify-steps-updated | verify-steps-setingame (the lab panel's guided "Step X of N" checklist, one scene per situation:
    /// no presets, the lab working, play once, the fix step without and with a mapping run, settings sync, all set, game updated, set in-game).
    /// verify-post-held | verify-post-changed | verify-post-recurring | verify-post-updated | verify-post-map | verify-post-consent | verify-post-asked
    /// (the post-session card, one per kind; post-asked: the lab was asked automatically).
    /// OPTIMUSVIBE_PAD=1 pretends a gamepad is connected (button glyphs and hints show).
    /// </summary>
    public static async Task RunSceneAsync(MainWindow window, MainViewModel vm)
    {
        if (Scene is not { Length: > 0 } scene || ShotPath is not { } shot) return;
        for (var i = 0; i < 100 && !(vm.IsReady && vm.Library.HasScanned); i++) await Task.Delay(200);
        await Task.Delay(500);
        if (Environment.GetEnvironmentVariable("OPTIMUSVIBE_PAD") == "1")
        {
            vm.IsGamepadConnected = true;
            OptimusVibe.App.Input.PadState.SetIsConnected(window, true);
        }
        // OPTIMUSVIBE_GAME=<game id> picks that game (to screenshot a real game page); otherwise the first managed tile.
        var pick = Environment.GetEnvironmentVariable("OPTIMUSVIBE_GAME");
        var tuned = (pick is { Length: > 0 } ? vm.Library.Tiles.FirstOrDefault(t => t.GameId == pick) : null)
                    ?? vm.Library.Tiles.FirstOrDefault(t => t.Status == TileStatus.Managed) ?? vm.Library.Tiles.FirstOrDefault();
        switch (scene)
        {
            case "library":
                break;
            case "signin-rejected":
                // the "GitHub sign-in stopped working" banner under the header, with a sample summary line
                vm.ShowSignInDemo();
                await Task.Delay(500);
                break;
            case "library-focus":
                window.FocusDefault();
                OptimusVibe.App.Input.FocusCue.SetShow(window, true);
                break;
            case "detail":
            case "detail-saves":
                if (tuned is null) break;
                vm.OpenLibraryDetail(tuned);
                await Task.Delay(800);
                if (scene == "detail-saves" && vm.SelectedDetail is { } d) d.SelectedTabIndex = GameDetailViewModel.ManualTab;
                break;
            case "detail-long-preset":
                // the summary row must stay one line with a long preset name (a manual save's date stamp, "Big Picture (TV)")
                if (tuned is null) break;
                vm.OpenLibraryDetail(tuned);
                await Task.Delay(800);
                window.LibraryDetail.GameDetail.ScrollToTop();
                vm.SelectedDetail?.DebugOverride("Mapped settings Oct 21 16:59 for the living room TV", null);
                await Task.Delay(300);
                break;
            case "detail-intent-focus":
            case "detail-intent-edit":
                // the intent card with focus (expanded over the top third) / being edited
                if (tuned is null) break;
                vm.OpenLibraryDetail(tuned);
                await Task.Delay(800);
                OptimusVibe.App.Input.FocusCue.SetShow(window, true);
                window.LibraryDetail.GameDetail.FocusIntentCard();
                window.LibraryDetail.GameDetail.ScrollToTop();
                await Task.Delay(300);
                if (scene == "detail-intent-edit" && vm.SelectedDetail is { } ed) ed.EditIntentCommand.Execute(null);
                await Task.Delay(500);
                break;
            case "detail-pad-down":
            case "detail-pad-up":
                // Issue #50: D-pad Down from the Recommended tab header must reach the first preset card; Up must reach the toggles above.
                if (tuned is null) break;
                vm.OpenLibraryDetail(tuned);
                await Task.Delay(800);
                window.LibraryDetail.GameDetail.FocusSelectedTab();
                OptimusVibe.App.Input.FocusCue.SetShow(window, true);
                var dir = scene == "detail-pad-up" ? System.Windows.Input.FocusNavigationDirection.Up : System.Windows.Input.FocusNavigationDirection.Down;
                var moved = System.Windows.Input.Keyboard.FocusedElement is UIElement from && window.TryMove(from, dir);
                if (scene == "detail-pad-up")
                    for (var step = 0; step < 2; step++)
                    {
                        // further Up presses take the gamepad navigator's normal route (MoveFocus)
                        (System.Windows.Input.Keyboard.FocusedElement as UIElement)?.MoveFocus(new System.Windows.Input.TraversalRequest(dir));
                        var at = System.Windows.Input.Keyboard.FocusedElement;
                        OptimusVibe.Logging.Log.Info($"debug scene {scene}: then Up -> {at?.GetType().Name} '{(at as System.Windows.Controls.ContentControl)?.Content}'");
                    }
                var now = System.Windows.Input.Keyboard.FocusedElement;
                OptimusVibe.Logging.Log.Info($"debug scene {scene}: moved={moved}, focus now {now?.GetType().Name} " +
                         $"'{(now as System.Windows.Controls.ContentControl)?.Content ?? (now as FrameworkElement)?.DataContext}'");
                await Task.Delay(300);
                break;
            case "manage":
                if (tuned is null) break;
                vm.OpenLibraryDetail(tuned);
                await Task.Delay(300);
                vm.CloseDetail();
                vm.ShowManageCommand.Execute(null);
                await Task.Delay(800);
                break;
            case "settings":
                vm.OpenSettings();
                await Task.Delay(1500);
                break;
            case "settings-appearance":
                vm.OpenSettings("appearance");
                await Task.Delay(1500);
                break;
            case "settings-lab":
                vm.OpenSettings("gamelab");
                await Task.Delay(3500);
                break;
            case "settings-lab-auto":
                // the "Automatic" toggles at the end of the game lab section
                vm.OpenSettings("gamelab");
                await Task.Delay(3500);
                if (Descendants<CheckBox>(window).FirstOrDefault(c => c.IsVisible &&
                        System.Windows.Automation.AutomationProperties.GetName(c) == "Request presets for new games automatically") is { } auto)
                {
                    auto.BringIntoView(new Rect(0, -60, auto.ActualWidth, 420));
                    await Task.Delay(500);
                }
                break;
            case "library-chips":
            {
                // the tiles' health chips ("Game updated", "Changed by game") and a working game's Play hint, with sample health
                var managed = vm.Library.Tiles.Where(t => t.Status == TileStatus.Managed && !t.IsSetInGame).Take(3).ToList();
                var samples = new[] { HealthState.GameUpdated, HealthState.GameChangedSettings, HealthState.Working };
                for (var i = 0; i < managed.Count; i++)
                    managed[i].ShowDemoHealth(new GameHealth
                    {
                        State = samples[i],
                        Headline = samples[i] == HealthState.Working ? "Working. Presets held on your last 3 launches." : "",
                    });
                await Task.Delay(300);
                foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible && s.ScrollableHeight > 0)) sv.ScrollToVerticalOffset(460);
                await Task.Delay(500);
                break;
            }
            case "report":
                vm.OpenReport(null, null);
                await Task.Delay(800);
                break;
            case "steam-review":
                vm.OfferSteamChanges(vm.Library.Tiles.Take(9).Select((t, i) =>
                    new OptimusVibe.Library.LaunchOptionChange(new[] { 1086940, 1091500, 1145350, 1285190, 1774580, 1888160, 220440, 2379780, 367520 }[i], null,
                        i % 3 == 0 ? null : "-novid", "\"OptimusVibe.exe\" run game -- %command%")).ToList());
                vm.ReviewSteamCommand.Execute(null);
                await Task.Delay(800);
                break;
            case "verify-confirm":
            case "verify-held":
            case "verify-overridden":
            case "verify-sent":
            case "verify-unknown":
            case "verify-hint":
            case "verify-status":
            case "verify-auto":
            case "verify-auto-first":
            case "verify-auto-forget":
            case "verify-suggest":
            case "verify-suggest-map":
            case "verify-suggest-result":
            case "verify-flag-confirm":
            case "verify-flagged":
            case "verify-flagged-done":
            case "verify-flagged-config":
            case "verify-health-working":
            case "verify-health-unchecked":
            case "verify-health-changed":
            case "verify-health-updated":
            case "verify-health-sync":
            case "verify-health-nopresets":
            case "verify-health-labworking":
            case "verify-health-more":
            case "verify-health-ambiguous":
            case "verify-health-verifynext":
            case "verify-health-changed-which":
            case "verify-health-evidence":
            case "verify-health-muted":
            case "verify-steps-nopresets":
            case "verify-steps-labworking":
            case "verify-steps-play":
            case "verify-steps-fix":
            case "verify-steps-fix-mapped":
            case "verify-steps-sync":
            case "verify-steps-allset":
            case "verify-steps-updated":
            case "verify-steps-setingame":
            case "verify-post-asked":
            case "verify-post-held":
            case "verify-post-changed":
            case "verify-post-recurring":
            case "verify-post-updated":
            case "verify-post-map":
            case "verify-post-consent":
                if (tuned is not null)
                {
                    vm.OpenLibraryDetail(tuned);
                    await Task.Delay(800);
                    tuned.Lab.ShowDemo(scene["verify-".Length..]);
                }
                else await Task.Delay(800); // no tiles (a config with no installed games): the manage page below still has a panel
                vm.SelectedDetail?.Lab.ShowDemo(scene["verify-".Length..]); // the manage page has its own panel
                await Task.Delay(500);
                if (Descendants<LabGamePanel>(window).FirstOrDefault(p => p.IsVisible) is { } panel)
                {
                    panel.BringIntoView(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
                    await Task.Delay(300);
                }
                break;
            case "map-first":
                if (tuned is null) break;
                vm.OpenLibraryDetail(tuned);
                await Task.Delay(800);
                tuned.Lab.ShowDemo("map-first");
                vm.SelectedDetail?.Lab.ShowDemo("map-first");
                await Task.Delay(500);
                if (Descendants<LabGamePanel>(window).FirstOrDefault(p => p.IsVisible) is { } mapPanel)
                {
                    mapPanel.BringIntoView(new Rect(0, 0, mapPanel.ActualWidth, mapPanel.ActualHeight));
                    await Task.Delay(300);
                }
                break;
            case "map-intro":
            case "map-launch2":
            case "map-summary":
            case "map-summary-kept":
            case "map-summary-none":
            case "map-sent":
            case "map-failed":
            case "map-passive":
                await ShowMapSceneAsync(scene["map-".Length..], shot);
                return;
            case "game-request":
                var tile = vm.Library.Tiles.FirstOrDefault(t => t.Status == TileStatus.NeedsPresets) ?? vm.Library.Tiles.FirstOrDefault();
                if (tile is null) break;
                vm.OpenLibraryDetail(tile);
                await Task.Delay(800);
                if (tile.Lab.RequestCommand.CanExecute(null)) tile.Lab.RequestCommand.Execute(null);
                await Task.Delay(3500);
                foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible && s.ScrollableHeight > 0)) sv.ScrollToVerticalOffset(sv.ExtentHeight);
                break;
        }
        vm.DismissToastCommand.Execute(null);
        await Task.Delay(500);
        Save((FrameworkElement)window.Content, shot);
        Application.Current.Shutdown();
    }
}
#endif
