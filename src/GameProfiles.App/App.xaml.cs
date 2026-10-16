using System.IO;
using System.Windows;
using GameProfiles.App.ViewModels;
using GameProfiles.App.Views;
using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Quiet = true; // WinExe: no console; everything goes to %LOCALAPPDATA%\GameProfiles\logs
        Log.Init(Path.Combine(AppPaths.DefaultDataDir, "logs"));
        Log.Info("GameProfiles app starting");

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

        base.OnStartup(e);
        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        _ = vm.LoadAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("GameProfiles app exiting");
        base.OnExit(e);
    }
}
