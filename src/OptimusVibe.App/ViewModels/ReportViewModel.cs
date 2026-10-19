using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using OptimusVibe.Devices;
using OptimusVibe.Feedback;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;
using OptimusVibe.Updates;

namespace OptimusVibe.App.ViewModels;

/// <summary>
/// "Report a problem" panel. The user writes a description, Preview shows the exact issue text (sanitized context + recent
/// log), and only a previewed, unchanged report can be sent. No GitHub sign-in: release builds file the issue on the APP repo
/// with the built-in bug-report token (limited to a few reports a day); other builds copy the report to the clipboard and open
/// a prefilled "new issue" page in the browser.
/// </summary>
public sealed class ReportViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isOpen;
    private string _description = "";
    private string? _gameId;
    private string? _preset;
    private BugReportDraft? _draft;
    private string? _draftDescription;
    private HardwareInfo? _hardware;

    public ReportViewModel(MainViewModel main)
    {
        _main = main;
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !main.IsBusy && Description.Trim().Length > 0);
        SendCommand = new AsyncRelayCommand(SendAsync, () => !main.IsBusy && CanSend);
        CloseCommand = new RelayCommand(Close);
    }

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public string TargetRepo => AppRepo.FullName;

    private string? _userToken;
    private string? _login;

    /// <summary>The report can be filed without leaving the app: as the signed-in user, else via the built-in token.</summary>
    public bool CanSendDirectly => BugReport.ResolveIdentity(_userToken) != BugReport.ReportIdentity.Browser;
    public string SendLabel => CanSendDirectly ? "Send" : "Copy and open GitHub";
    public string SendInfoText
    {
        get
        {
            var who = BugReport.DescribeIdentity(BugReport.ResolveIdentity(_userToken), _login);
            return CanSendDirectly
                ? $"{who}. It becomes a public issue on {AppRepo.FullName}. Nothing else leaves your PC."
                : $"{who}. The report is also copied to your clipboard.";
        }
    }

    /// <summary>Which identity will be used: the signed-in token if there is one (read off the UI thread when the panel opens).</summary>
    private async Task RefreshIdentityAsync()
    {
        try
        {
            _userToken = await Task.Run(() =>
            {
                try { return OptimusVibe.Sync.GitHubAuth.LoadToken(); }
                catch (Exception) { return null; }
            });
            _login = _userToken is null ? null : (await Services.SettingsStore.LoadAsync()).GitHubLogin;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the GitHub sign-in for the report failed: {ex.Message}");
            _userToken = null;
        }
        OnPropertyChanged(nameof(CanSendDirectly));
        OnPropertyChanged(nameof(SendLabel));
        OnPropertyChanged(nameof(SendInfoText));
        CommandManager.InvalidateRequerySuggested();
    }

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

    public bool CanSend => _draft is not null && _draftDescription == Description && Description.Trim().Length > 0;

    public ICommand PreviewCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CloseCommand { get; }

    public void Open(string? gameId, string? preset)
    {
        GameId = gameId;
        Preset = preset;
        ClearDraft();
        IsOpen = true;
        _ = RefreshIdentityAsync();
    }

    public void Close()
    {
        IsOpen = false;
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
        if (!CanSendDirectly)
        {
            HandOffToBrowser(draft);
            return;
        }
        BugReportOutcome? outcome = null;
        var ok = await _main.RunAsync("Sending the report", async () => outcome = await OutboxFlow.SendBugReportAsync(_main.Outbox.Store, draft, _userToken));
        if (!ok || outcome is null) return;
        if (outcome.WasQueued)
        {
            Log.Info("Bug report queued (offline)");
            Description = "";
            ClearDraft();
            Close();
            _main.Outbox.Service.NotifyQueued();
            _main.Notify("You're offline - the report is saved and will be sent when you're back online.");
            return;
        }
        var url = outcome.Url!;
        Log.Info($"Bug report filed: {url}");
        Description = "";
        ClearDraft();
        Close();
        _main.NotifyWithAction($"Report sent: {url}", "Open", () => OpenUrl(url));
    }

    /// <summary>No built-in token: full report to the clipboard, prefilled (possibly shortened) issue form in the browser.</summary>
    private void HandOffToBrowser(BugReportDraft draft)
    {
        var copied = false;
        try
        {
            Clipboard.SetText(draft.Title + "\n\n" + draft.Body);
            copied = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Clipboard: {ex.Message}");
        }
        var link = BugReport.BuildNewIssueUrl(draft, out var truncated);
        OpenUrl(link);
        Log.Info("Bug report handed to the browser (prefilled issue form)");
        Description = "";
        ClearDraft();
        Close();
        _main.Notify(truncated && copied
            ? "The report is on your clipboard and GitHub is open. It was shortened to fit the link: paste the full text into the issue."
            : copied
                ? "GitHub is open with your report filled in (a copy is on your clipboard). Press Submit new issue."
                : "GitHub is open with your report filled in. Press Submit new issue.");
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error("Opening the browser failed", ex);
            _main.Notify($"Couldn't open the browser - go to {AppRepo.IssuesUrl}/new", error: true);
        }
    }
}
