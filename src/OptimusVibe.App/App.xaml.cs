using System.IO;
using System.Windows;
using OptimusVibe.App.Services;
using OptimusVibe.App.ViewModels;
using OptimusVibe.App.Views;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Quiet = true; // WinExe: no console; everything goes to %LOCALAPPDATA%\OptimusVibe\logs
        Log.Init(Path.Combine(AppPaths.DefaultDataDir, "logs"));
        LegacyMigration.Run(); // one-time copy of the pre-rename GameProfiles data folder, before anything reads settings

        // The post-game "keep your changes?" prompt: a full-screen controller-friendly window (the CLI keeps its message box).
        OptimusVibe.Launch.CapturePrompts.Current = new WpfCapturePrompt();

#if DEBUG
        if (e.Args.Length > 0 && e.Args[0] == "capture-demo")
        {
            Shutdown(DebugScenes.RunCaptureDemo(this));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "notes-demo")
        {
            DebugScenes.RunNotesDemo(this, e.Args.Length > 1 ? e.Args[1] : null);
            return;
        }
#endif

        // Steam launch option: `OptimusVibe.exe run <game> -- %command%` — no window, no console flash.
        if (HeadlessRun.IsRunInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // the prompt window must not end the process before the run is finished
            int code;
            try { code = HeadlessRun.Execute(e.Args); }
            catch (Exception ex) { Log.Error("run failed", ex); code = 1; }
            Shutdown(code);
            return;
        }

        Log.Info("Optimus-Vibe app starting");

        // Remove the .old exe / partial download a previous self-update left behind (not on the headless `run` path).
        if (Environment.ProcessPath is { } exePath)
            _ = Task.Run(() => OptimusVibe.Updates.UpdateService.CleanupOld(exePath));

        // Troubleshooting / remote sessions where GPU composition shows a blank window.
        if (AppPaths.Env("SOFTWARE_RENDER") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Never crash: log, tell the user in the status bar, keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            (MainWindow?.DataContext as MainViewModel)?.Notify($"Unexpected error: {args.Exception.Message}", error: true);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Per-machine colour theme (Settings > Appearance), before the first window exists.
        try { var saved = SettingsStore.Load(); ThemeManager.Apply(saved.Theme); Density.SetMode(saved.Density); }
        catch (Exception ex) { Log.Warn($"Theme not applied: {ex.Message}"); }

        base.OnStartup(e);
        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        _ = vm.LoadAsync();
#if DEBUG
        _ = DebugScenes.RunSceneAsync(window, vm);
#endif
        _ = vm.Updates.BackgroundCheckAsync(); // at most once a day; shows the header badge when a release is newer
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Optimus-Vibe app exiting");
        base.OnExit(e);
    }
}
