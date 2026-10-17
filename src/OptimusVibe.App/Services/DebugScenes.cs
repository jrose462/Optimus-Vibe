#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OptimusVibe.App.ViewModels;
using OptimusVibe.App.Views;
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
        ThemeManager.Apply(SettingsStore.Load().Theme);
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    /// <summary>Runs the named scene against the main window once it is ready, saves the screenshot and exits.</summary>
    public static async Task RunSceneAsync(MainWindow window, MainViewModel vm)
    {
        if (Scene is not { Length: > 0 } scene || ShotPath is not { } shot) return;
        for (var i = 0; i < 100 && !(vm.IsReady && vm.Library.HasScanned); i++) await Task.Delay(200);
        await Task.Delay(500);
        switch (scene)
        {
            case "settings-lab":
                vm.OpenSettings("gamelab");
                await Task.Delay(3500);
                break;
            case "game-request":
                var tile = vm.Library.Tiles.FirstOrDefault(t => t.Status == TileStatus.NeedsPresets) ?? vm.Library.Tiles.FirstOrDefault();
                if (tile is null) break;
                vm.OpenLibraryDetail(tile);
                await Task.Delay(800);
                if (tile.Lab.RequestCommand.CanExecute(null)) tile.Lab.RequestCommand.Execute(null);
                await Task.Delay(3500);
                break;
        }
if (scene == "game-request")            foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible && s.ScrollableHeight > 0)) sv.ScrollToVerticalOffset(sv.ExtentHeight);        vm.DismissToastCommand.Execute(null);        await Task.Delay(500);
        Save((FrameworkElement)window.Content, shot);
        Application.Current.Shutdown();
    }
}
#endif
