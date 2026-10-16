using System.Diagnostics;
using System.Windows.Input;
using OptimusVibe.Devices;
using OptimusVibe.Feedback;
using OptimusVibe.Logging;
using OptimusVibe.Sync;
using OptimusVibe.Updates;

namespace OptimusVibe.App.ViewModels;

/// <summary>
/// "Report a problem" panel. The user writes a description, Preview shows the exact issue text (sanitized context + recent
/// log), and only a previewed, unchanged report can be sent - as an issue on the APP repo through the signed-in account.
/// </summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly GitHubAccountViewModel _account;
    private bool _isOpen;
    private string _description = "";
    private string? _gameId;
    private string? _preset;
    private BugReportDraft? _draft;
    private string? _draftDescription;
    private bool _isSignedIn;
    private HardwareInfo? _hardware;

    public ReportViewModel(MainViewModel main, GitHubAccountViewModel account)
    {
        _main = main;
        _account = account;
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !main.IsBusy && Description.Trim().Length > 0);
        SendCommand = new AsyncRelayCommand(SendAsync, () => !main.IsBusy && CanSend);
        CloseCommand = new RelayCommand(Close);
        GoToSignInCommand = new RelayCommand(() =>
        {
            Close();
            main.OpenSettings("account");
        });
    }

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public string TargetRepo => AppRepo.FullName;

    public string Description
    {
        get => _description;
        set
        {
            if (!Set(ref _description, value)) return;
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(PreviewIsStale));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsSignedIn { get => _isSignedIn; private set { if (Set(ref _isSignedIn, value)) OnPropertyChanged(nameof(IsSignedOut)); } }
    public bool IsSignedOut => !IsSignedIn;

    public string? GameId { get => _gameId; private set { if (Set(ref _gameId, value)) OnPropertyChanged(nameof(ContextText)); } }
    public string? Preset { get => _preset; private set { if (Set(ref _preset, value)) OnPropertyChanged(nameof(ContextText)); } }

    public string ContextText => GameId is null
        ? "Not about a specific game."
        : $"About {GameId}{(Preset is { Length: > 0 } p ? $" · preset {p}" : "")}";

    public bool HasPreview => _draft is not null;
    public string PreviewTitle => _draft?.Title ?? "";
    public string PreviewText => _draft is null ? "" : _draft.Body;

    /// <summary>The text changed after the preview: the preview no longer matches what would be sent.</summary>
    public bool PreviewIsStale => _draft is not null && _draftDescription != Description;

    public bool CanSend => IsSignedIn && _draft is not null && _draftDescription == Description && Description.Trim().Length > 0;

    public ICommand PreviewCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand GoToSignInCommand { get; }

    public void Open(string? gameId, string? preset)
    {
        GameId = gameId;
        Preset = preset;
        ClearDraft();
        IsOpen = true;
        _ = RefreshSignInAsync();
    }

    public void Close()
    {
        IsOpen = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task RefreshSignInAsync()
    {
        await _account.RefreshAsync();
        IsSignedIn = _account.IsSignedIn;
        OnPropertyChanged(nameof(CanSend));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearDraft() => SetDraft(null, null);

    private void SetDraft(BugReportDraft? draft, string? description)
    {
        _draft = draft;
        _draftDescription = description;
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewIsStale));
        OnPropertyChanged(nameof(CanSend));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task PreviewAsync()
    {
        var description = Description;
        var session = _main.Session;
        var gameId = GameId;
        var preset = Preset;
        BugReportDraft? draft = null;
        var ok = await _main.RunAsync("Preparing the report preview", () => Task.Run(() =>
        {
            _hardware ??= SafeDetect();
            var ctx = BugReportContext.Collect("app", session?.Device, _hardware, gameId, preset);
            draft = BugReport.Build(description, ctx);
        }));
        if (ok && draft is not null) SetDraft(draft, description);
    }

    private static HardwareInfo? SafeDetect()
    {
        try { return DeviceDetector.Detect(); }
        catch (Exception ex)
        {
            Log.Warn($"Hardware detection for the report failed: {ex.Message}");
            return null;
        }
    }

    private async Task SendAsync()
    {
        var draft = _draft;
        if (draft is null || !CanSend) return;
        string? url = null;
        var ok = await _main.RunAsync("Sending the report", async () =>
        {
            var token = await Task.Run(() =>
            {
                try { return GitHubAuth.LoadToken(); }
                catch (NotImplementedException) { return null; }
            }) ?? throw new InvalidOperationException("Sign in to GitHub first (Settings, GitHub account).");
            url = await BugReport.SubmitAsync(draft, token);
        });
        if (!ok || url is null) return;
        Log.Info($"Bug report filed: {url}");
        Description = "";
        ClearDraft();
        Close();
        _main.NotifyWithAction($"Report sent: {url}", "Open", () => OpenUrl(url));
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error($"Opening {url} failed", ex);
            _main.Notify($"Couldn't open the browser - go to {url}", error: true);
        }
    }
}
