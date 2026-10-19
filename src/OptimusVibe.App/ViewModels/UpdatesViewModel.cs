using System.IO;
using System.Windows;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Config;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe.App.ViewModels;

/// <summary>
/// App updates (Settings card + header badge): check the APP repo's releases, show the notes, download, swap and restart.
/// Everything that touches the network or disk runs off the UI thread; failures become status text, never exceptions.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private UpdateInfo? _update;
    private string _statusText = "";
    private bool _statusIsError;
    private bool _isChecking;
    private bool _isDownloading;
    private double _progress;

    public UpdatesViewModel(MainViewModel main)
    {
        _main = main;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !main.IsBusy && !IsChecking);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !main.IsBusy && IsAvailable);
        ShowCommand = new RelayCommand(() => main.OpenSettings("updates"));
        PopOutNotesCommand = new RelayCommand(
            () => Views.ReleaseNotesWindow.Show(Update is { } u ? $"What's new in {u.Version}" : "Release notes", NotesText, Application.Current?.MainWindow),
            () => Update is not null);
    }

    /// <summary>Open the release notes in their own window (Markdown rendered, reading size).</summary>
    public ICommand PopOutNotesCommand { get; }

    public string VersionText => $"Optimus-Vibe {AppVersion.Current}";
    public string RepoText => AppRepo.FullName;

    public UpdateInfo? Update
    {
        get => _update;
        private set
        {
            if (!Set(ref _update, value)) return;
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(AvailableText));
            OnPropertyChanged(nameof(NotesText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>A newer release was found: show notes + Download and restart, and the header badge.</summary>
    public bool IsAvailable => Update is { IsNewer: true };

    public string AvailableText => Update is { } u ? $"Version {u.Version} is available (you have {u.CurrentVersion})" : "";
    public string NotesText => Update is { } u && !string.IsNullOrWhiteSpace(u.Notes) ? u.Notes.Trim() : "No release notes.";

    public string StatusText { get => _statusText; private set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => StatusText.Length > 0;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public bool IsChecking { get => _isChecking; private set { if (Set(ref _isChecking, value)) CommandManager.InvalidateRequerySuggested(); } }

    public bool IsDownloading { get => _isDownloading; private set => Set(ref _isDownloading, value); }

    /// <summary>0..1 while downloading.</summary>
    public double Progress
    {
        get => _progress;
        private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    public string ProgressText => $"Downloading… {Progress:P0}";

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand ShowCommand { get; }

    // ---------------------------------------------------------------- check

    private static async Task<UpdateCheckResult> RunCheckAsync()
    {
        try
        {
            return await Task.Run(async () => await UpdateService.CheckAsync());
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.Failed, $"Couldn't check for updates: {ex.Message}", null);
        }
    }

    private void Apply(UpdateCheckResult r)
    {
        Update = r.UpdateAvailable ? r.Update : null;
        StatusIsError = r.Status is UpdateStatus.Failed;
        StatusText = r.Status == UpdateStatus.UpdateAvailable ? "" : r.Message;
    }

    private async Task CheckAsync()
    {
        IsChecking = true;
        StatusIsError = false;
        StatusText = "Checking for updates…";
        try
        {
            var r = await RunCheckAsync();
            Apply(r);
            if (r.Status != UpdateStatus.Failed) await Task.Run(UpdateCheckStore.MarkChecked);
            Log.Info($"Update check: {r.Status}");
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Startup: at most once a day, no toast and no status text unless an update exists (header badge).</summary>
    public async Task BackgroundCheckAsync()
    {
        try
        {
            if (AppPaths.Env("NO_UPDATE_CHECK") == "1") return;
            if (!await Task.Run(UpdateCheckStore.IsDue)) return;
            var r = await RunCheckAsync();
            if (r.Status != UpdateStatus.Failed) await Task.Run(UpdateCheckStore.MarkChecked);
            Log.Info($"Background update check: {r.Status}");
            if (r.UpdateAvailable) Apply(r);
        }
        catch (Exception ex)
        {
            Log.Warn($"Background update check: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- install

    private async Task InstallAsync()
    {
        var info = Update;
        var exe = Environment.ProcessPath;
        if (info is null || exe is null) return;
        // `dotnet run` / tests: ProcessPath is dotnet.exe - never stage an update next to that.
        if (!Path.GetFileName(exe).Equals(AppRepo.AppAsset, StringComparison.OrdinalIgnoreCase))
        {
            _main.Notify($"Updates only apply to the installed {AppRepo.AppAsset} (this is {Path.GetFileName(exe)}).", error: true);
            return;
        }

        Progress = 0;
        IsDownloading = true;
        var ok = false;
        try
        {
            var progress = new Progress<double>(p => Progress = p); // created on the UI thread: reports marshal back to it
            ok = await _main.RunAsync($"Downloading Optimus-Vibe {info.Version}", async () =>
            {
                var staged = await UpdateService.StageAllAsync(info, exe, progress);
                foreach (var note in staged.Notes) Log.Info($"update: {note}");
                await Task.Run(() => UpdateService.ApplyStagedAndRestart(exe, []));
            });
        }
        finally
        {
            IsDownloading = false;
        }
        if (ok)
        {
            Log.Info($"Updated to {info.Version}; shutting down for the restart");
            Application.Current.Shutdown();
        }
    }
}
