using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.GameLab;
using OptimusVibe.Logging;

namespace OptimusVibe.App.ViewModels;

/// <summary>What the per-game panel needs to know about its game right now.</summary>
/// <param name="Target">Null when the game can't be described (nothing installed and no config).</param>
/// <param name="HasConfig">The game has a config file here.</param>
/// <param name="NeedsPresets">No presets for this device (no config, or a config without this device).</param>
public sealed record LabGameContext(string Name, string? Key, string? GameId, LabTarget? Target, bool HasConfig, bool NeedsPresets);

/// <summary>
/// The game-lab actions on one game's page: Request / Refresh presets, Re-check settings sync, the confirmation with the evidence
/// preview (counts and tokenized paths, no contents), and the request's status chip with a link to its issue.
/// </summary>
public sealed class LabGameViewModel : ObservableObject
{
    private readonly GameLabViewModel _lab;
    private readonly MainViewModel _main;
    private readonly Func<LabGameContext> _context;
    private LabKind? _confirming;
    private bool _loadingPreview;
    private EvidencePreview? _preview;
    private string? _previewError;
    private int _generation;

    public LabGameViewModel(GameLabViewModel lab, MainViewModel main, Func<LabGameContext> context)
    {
        _lab = lab;
        _main = main;
        _context = context;
        bool Idle() => !main.IsBusy;
        RequestCommand = new AsyncRelayCommand(() => BeginAsync(Ctx.HasConfig ? LabKind.Refresh : LabKind.New), () => Idle() && CanAct && CanRequestPresets);
        RefreshPresetsCommand = new AsyncRelayCommand(() => BeginAsync(LabKind.Refresh), () => Idle() && CanAct && CanRefreshPresets);
        SyncCheckCommand = new AsyncRelayCommand(() => BeginAsync(LabKind.SyncCheck), () => Idle() && CanAct && CanSyncCheck);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => Idle() && Preview is not null && Ctx.Target is not null);
        CancelCommand = new RelayCommand(() => CancelTransient());
        OpenIssueCommand = new RelayCommand(() => { if (Issue is { } i) _lab.OpenUrl(i.Url); }, () => Issue is not null);
        OpenSettingsCommand = lab.OpenSettingsCommand;
        lab.IssuesChanged += Refresh;
        lab.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(GameLabViewModel.IsReady)) Refresh(); };
    }

    private LabGameContext Ctx => _context();

    public ICommand RequestCommand { get; }
    public ICommand RefreshPresetsCommand { get; }
    public ICommand SyncCheckCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenIssueCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public void Refresh() => OnPropertyChanged(string.Empty);

    // ---------------------------------------------------------------- what is offered

    public bool LabReady => _lab.IsReady;
    public bool CanAct => LabReady && Ctx.Target is not null;
    public bool CanRequestPresets => Ctx.NeedsPresets || !Ctx.HasConfig;
    public bool CanRefreshPresets => Ctx.HasConfig && !Ctx.NeedsPresets;
    public bool CanSyncCheck => Ctx.HasConfig;
    public bool ShowRequest => CanRequestPresets;
    public bool ShowRefresh => CanRefreshPresets;
    public bool ShowSyncCheck => CanSyncCheck;

    public string RequestText => Ctx.HasConfig ? "Request presets for this device" : "Request presets";
    public string DisabledReason => LabReady
        ? (Ctx.Target is null ? "This game isn't installed here, so there is nothing to collect." : "")
        : "The game lab isn't set up yet. Set it up in Settings and Claude can research this game for you.";
    public bool IsDisabled => DisabledReason.Length > 0;
    public bool NeedsSetup => !LabReady;

    // ---------------------------------------------------------------- status chip

    public LabIssue? Issue => LabStatusIndex.For(_lab.Issues, Ctx.Key, Ctx.GameId);
    public bool HasChip => Issue is not null;
    public string ChipText => Issue is { } i ? LabStatusIndex.Chip(i) : "";
    public string ChipLine => Issue is { } i ? $"Game lab: {ChipText.ToLowerInvariant()} (#{i.Number})" : "";
    public bool ChipNeedsAttention => Issue is { } i && i.IsOpen && i.Status is LabStatus.NeedsEvidence or LabStatus.Failed;

    // ---------------------------------------------------------------- confirmation

    public bool IsConfirming => _confirming is not null;
    public bool IsLoadingPreview { get => _loadingPreview; private set => Set(ref _loadingPreview, value); }
    public EvidencePreview? Preview { get => _preview; private set { if (Set(ref _preview, value)) OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => Preview is not null;
    public string? PreviewError { get => _previewError; private set { if (Set(ref _previewError, value)) OnPropertyChanged(nameof(HasPreviewError)); } }
    public bool HasPreviewError => PreviewError is not null;

    public string ConfirmTitle => _confirming switch
    {
        LabKind.SyncCheck => $"Re-check settings sync for {Ctx.Name}?",
        LabKind.Refresh => $"Refresh presets for {Ctx.Name}?",
        _ => $"Request presets for {Ctx.Name}?",
    };

    public string ConfirmLead => _confirming switch
    {
        LabKind.SyncCheck => "Claude looks again at whether this game's settings travel between your devices. Your presets stay as they are.",
        LabKind.Refresh => "Claude researches the game again (after a patch, or with better evidence) and rewrites the presets.",
        _ => "Claude researches the game's settings and writes presets for your devices. This takes a few minutes and you'll get a notice when they arrive.",
    };

    public string SendText => "Send request";

    public string PrivacyNote => $"Evidence goes only to your private config repo{(_lab.RepoName is { } r ? $" ({r})" : "")}.";

    public bool CancelTransient()
    {
        var any = IsConfirming;
        _generation++;
        _confirming = null;
        Preview = null;
        PreviewError = null;
        IsLoadingPreview = false;
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ConfirmTitle));
        CommandManager.InvalidateRequerySuggested();
        return any;
    }

    private async Task BeginAsync(LabKind kind)
    {
        var ctx = Ctx;
        if (ctx.Target is null) return;
        var gen = ++_generation;
        _confirming = kind;
        Preview = null;
        PreviewError = null;
        IsLoadingPreview = true;
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ConfirmTitle));
        OnPropertyChanged(nameof(ConfirmLead));
        OnPropertyChanged(nameof(PrivacyNote));
        try
        {
            var preview = await _lab.PreviewAsync(ctx.Target);
            if (gen != _generation) return;
            Preview = preview;
        }
        catch (Exception ex)
        {
            Log.Warn($"Evidence preview failed: {ex.Message}");
            if (gen == _generation) PreviewError = $"Couldn't look at this PC: {ex.Message}";
        }
        finally
        {
            if (gen == _generation) IsLoadingPreview = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ConfirmAsync()
    {
        var ctx = Ctx;
        if (_confirming is not { } kind || ctx.Target is null) return;
        var outcome = await _lab.FileRequestAsync(kind, ctx.Target);
        if (outcome is null) return;
        CancelTransient();
        if (outcome.Filed is not { } filed)
        {
            _main.Notify("Saved - it will be sent to the game lab when you're back online.");
            return;
        }
        if (filed.Reused) _main.NotifyWithAction(filed.Message, "Open", () => _lab.OpenUrl(filed.Url));
        else _main.NotifyWithAction($"Sent {ctx.Name} to the game lab. {filed.Message}", "Open issue", () => _lab.OpenUrl(filed.Url));
    }
}
