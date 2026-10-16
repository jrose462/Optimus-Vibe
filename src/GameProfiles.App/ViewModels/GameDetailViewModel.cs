using System.Collections.ObjectModel;
using System.Windows.Input;
using GameProfiles.App.Services;
using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Logging;

namespace GameProfiles.App.ViewModels;

/// <summary>The right-hand pane for one game: Recommended / Manual saves tabs, the diff panel, save form.</summary>
public sealed class GameDetailViewModel : ObservableObject
{
    public const int RecommendedTab = 0;
    public const int ManualTab = 1;

    private GameSnapshot _snap;
    private int _selectedTabIndex;
    private PreviewViewModel? _preview;
    private bool _isSaveFormOpen;
    private string _newSaveName = "";
    private string _newSaveNotes = "";
    private string? _lastSyncText;

    public GameDetailViewModel(IAppShell shell, GameSnapshot snapshot)
    {
        Shell = shell;
        _snap = snapshot;
        bool Idle() => !shell.IsBusy;
        OpenSaveFormCommand = new RelayCommand(() =>
        {
            CancelTransient();
            NewSaveName = $"Saved {DateTime.Now:MMM d HH:mm}";
            NewSaveNotes = "";
            IsSaveFormOpen = true;
        }, () => Idle() && _snap.Context is not null);
        SaveCurrentCommand = new AsyncRelayCommand(SaveCurrentAsync, () => Idle() && _snap.Context is not null);
        CancelSaveFormCommand = new RelayCommand(() => IsSaveFormOpen = false);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => Idle() && CanLaunch);
        UseDefaultCommand = new AsyncRelayCommand(UseDefaultAsync, () => Idle() && CanUseDefault);
        NextTabCommand = new RelayCommand(() => SelectedTabIndex = (SelectedTabIndex + 1) % 2);
        PreviousTabCommand = new RelayCommand(() => SelectedTabIndex = (SelectedTabIndex + 1) % 2); // two tabs: previous == next
        Rebuild();
    }

    public IAppShell Shell { get; }
    private AppSession Session => _snap.Session;
    private string DeviceName => Session.Device.Name;

    // ---------------------------------------------------------------- display

    public string GameId => _snap.Id;
    public string Name => _snap.Name;
    public string StoreText => GameItemViewModel.StoreLabel(_snap.Game?.Store);
    public string? Error => _snap.Error;
    public bool HasError => _snap.Error is not null;
    public bool HasGame => _snap.Context is not null;
    public string Device => DeviceName;

    public string ActiveText
    {
        get
        {
            var ctx = _snap.Context;
            if (ctx is null) return "";
            if (ctx.Profile.IsEmpty) return "No preset for this device";
            var label = ctx.Preset?.Kind == PresetKind.Manual ? $"Manual: {ctx.Save?.Name ?? ctx.Preset.Name}" : ctx.Profile.Label ?? ctx.Preset?.Name;
            return $"Active: {label}{(ctx.Profile.TargetFps is { } f ? $"  ·  {f} fps" : "")}{(ctx.IsSelected ? "" : "  (device default)")}";
        }
    }

    public string? IntentText => _snap.Game?.Intent.GetValueOrDefault(DeviceName) is { Length: > 0 } t ? t.Trim() : null;
    public bool HasIntent => IntentText is not null;

    public string? RecommendationText
    {
        get
        {
            if (_snap.Game?.Recommendation is not { } r) return null;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Generated)) parts.Add($"Generated {r.Generated}");
            if (!string.IsNullOrWhiteSpace(r.GameVersion)) parts.Add($"game version {r.GameVersion}");
            if (r.Sources.Count > 0) parts.Add($"{r.Sources.Count} source(s)");
            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }

    public bool HasRecommendation => RecommendationText is not null;
    public IReadOnlyList<string> Sources => _snap.Game?.Recommendation?.Sources ?? [];

    public bool CanLaunch => _snap.Game is { } g && GameLauncher.CanLaunch(g);
    public bool CanUseDefault => _snap.Context?.IsSelected == true && GameContext.DefaultFor(_snap.Context.Game, Session.Device) is not null;
    public string? DefaultPresetName => _snap.Context is { } c ? GameContext.DefaultFor(c.Game, Session.Device) : null;

    public string LaunchHint => _snap.Game?.SteamAppId is not null
        ? "Steam launch options apply the active preset"
        : "Launched through gameprofiles run";

    public ObservableCollection<PresetCardViewModel> Recommended { get; } = new();
    public ObservableCollection<PresetCardViewModel> Manual { get; } = new();
    public bool HasRecommended => Recommended.Count > 0;
    public bool HasManual => Manual.Count > 0;

    public int SelectedTabIndex { get => _selectedTabIndex; set => Set(ref _selectedTabIndex, value); }

    public PreviewViewModel? Preview
    {
        get => _preview;
        private set
        {
            if (Set(ref _preview, value)) OnPropertyChanged(nameof(IsPreviewOpen));
        }
    }

    public bool IsPreviewOpen => Preview is not null;

    public bool IsSaveFormOpen { get => _isSaveFormOpen; set => Set(ref _isSaveFormOpen, value); }
    public string NewSaveName { get => _newSaveName; set => Set(ref _newSaveName, value); }
    public string NewSaveNotes { get => _newSaveNotes; set => Set(ref _newSaveNotes, value); }

    public string? LastSyncText
    {
        get => _lastSyncText;
        private set
        {
            if (Set(ref _lastSyncText, value)) OnPropertyChanged(nameof(HasLastSync));
        }
    }

    public bool HasLastSync => LastSyncText is not null;

    public ICommand OpenSaveFormCommand { get; }
    public ICommand SaveCurrentCommand { get; }
    public ICommand CancelSaveFormCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand UseDefaultCommand { get; }
    public ICommand NextTabCommand { get; }
    public ICommand PreviousTabCommand { get; }

    public void Update(GameSnapshot snapshot)
    {
        _snap = snapshot;
        Rebuild();
        OnPropertyChanged(string.Empty);
    }

    private void Rebuild()
    {
        bool selected = _snap.Context?.IsSelected == true;
        SyncCards(Recommended, _snap.Recommended, selected);
        SyncCards(Manual, _snap.Manual, selected);
    }

    /// <summary>Update cards in place (matching by preset ref) so the focused button survives a reload.</summary>
    private void SyncCards(ObservableCollection<PresetCardViewModel> cards, IReadOnlyList<PresetOption> options, bool selected)
    {
        for (int i = cards.Count - 1; i >= 0; i--)
            if (!options.Any(o => o.Ref == cards[i].Option.Ref)) cards.RemoveAt(i);
        for (int i = 0; i < options.Count; i++)
        {
            var at = -1;
            for (int j = 0; j < cards.Count; j++)
                if (cards[j].Option.Ref == options[i].Ref) { at = j; break; }
            if (at < 0)
            {
                cards.Insert(i, new PresetCardViewModel(this, options[i], selected));
                continue;
            }
            if (at != i) cards.Move(at, i);
            cards[i].Update(options[i], selected);
        }
    }

    /// <summary>Close the diff panel, save form, rename or delete confirmation. True if anything was open.</summary>
    public bool CancelTransient()
    {
        bool any = Preview is not null || IsSaveFormOpen;
        Preview = null;
        IsSaveFormOpen = false;
        foreach (var c in Recommended.Concat(Manual)) any |= c.CancelTransient();
        return any;
    }

    public void ClosePreview() => Preview = null;

    // ---------------------------------------------------------------- operations (all Core work off the UI thread)

    /// <summary>Same as `gameprofiles use`: validate the preset loads, then remember it.</summary>
    internal async Task UseAsync(PresetCardViewModel card)
    {
        var session = Session;
        var gameId = GameId;
        var ok = await Shell.RunAsync($"Switching to {card.Label}", () => Task.Run(() =>
        {
            GameContext.Load(session.Store, gameId, session.Device, card.Option.Ref); // throws if it no longer exists
            new SelectionStore(session.Paths.SelectionsFile).Set(session.Device.Name, gameId, card.Option.Ref);
            Log.Info($"{gameId} on {session.Device.Name}: now uses {card.Option.Ref}");
        }));
        if (!ok) return;
        await Shell.ReloadGameAsync(gameId);
        Shell.Notify($"{Name} now uses {card.Label} — applied on next launch.");
    }

    private async Task UseDefaultAsync()
    {
        var session = Session;
        var gameId = GameId;
        var ok = await Shell.RunAsync("Resetting to device default", () => Task.Run(() =>
            new SelectionStore(session.Paths.SelectionsFile).Set(session.Device.Name, gameId, null)));
        if (!ok) return;
        await Shell.ReloadGameAsync(gameId);
        Shell.Notify($"{Name} is back to the device default ({DefaultPresetName}).");
    }

    internal async Task OpenPreviewAsync(PresetCardViewModel card, bool applyMode)
    {
        CancelTransient();
        var session = Session;
        var gameId = GameId;
        ApplyPreview? preview = null;
        var ok = await Shell.RunAsync($"Reading current settings for {card.Label}", () => Task.Run(() =>
        {
            var ctx = GameContext.Load(session.Store, gameId, session.Device, card.Option.Ref);
            preview = ApplyNow.Preview(ctx);
        }));
        if (ok && preview is not null) Preview = new PreviewViewModel(this, card, preview, applyMode);
    }

    internal async Task ApplyAsync(PreviewViewModel p)
    {
        var session = Session;
        var gameId = GameId;
        var alsoUse = p.ShowAlsoUse && p.AlsoUse;
        var ok = await Shell.RunAsync($"Applying {p.Card.Label}", () => Task.Run(() =>
        {
            Log.Info($"App: apply {p.Card.Option.Ref} to {gameId} on {session.Device.Name}");
            ApplyNow.Execute(p.Preview);
            if (alsoUse) new SelectionStore(session.Paths.SelectionsFile).Set(session.Device.Name, gameId, p.Card.Option.Ref);
        }));
        if (!ok) return;
        Preview = null;
        await Shell.ReloadGameAsync(gameId);
        var skipped = p.Files.Count(f => f.IsSkipped);
        Shell.Notify($"Applied {p.Card.Label} to {Name}: {p.ChangeCount} setting(s) written" +
                     (skipped > 0 ? $", {skipped} file(s) skipped" : "") + (alsoUse ? "; now the active preset." : "."));
    }

    private async Task SaveCurrentAsync()
    {
        var session = Session;
        var gameId = GameId;
        var name = NewSaveName;
        var notes = string.IsNullOrWhiteSpace(NewSaveNotes) ? null : NewSaveNotes.Trim();
        SaveResult? result = null;
        var ok = await Shell.RunAsync("Saving current settings (and syncing git)", () => Task.Run(() =>
        {
            var ctx = GameContext.Load(session.Store, gameId, session.Device);
            result = ManualSaves.SaveCurrent(ctx, name, notes: notes);
        }));
        if (!ok || result is null) return;
        IsSaveFormOpen = false;
        LastSyncText = SyncText(result.Sync);
        await Shell.ReloadGameAsync(gameId);
        SelectedTabIndex = ManualTab;
        Shell.Notify($"Saved “{result.Save.Name}”: {result.KeysSaved} setting(s)" +
                     (result.Snapshots.Count > 0 ? $", {result.Snapshots.Count} whole file(s)" : "") +
                     (result.Skipped.Count > 0 ? $", {result.Skipped.Count} skipped" : "") +
                     $" — now active. {LastSyncText}", result.Sync is { Ok: false });
    }

    internal async Task UpdateFromCurrentAsync(PresetCardViewModel card)
    {
        if (card.Slug is not { } slug) return;
        CancelTransient();
        var session = Session;
        var gameId = GameId;
        SaveResult? result = null;
        var ok = await Shell.RunAsync($"Updating “{card.Label}” from the game's current settings", () => Task.Run(() =>
        {
            var ctx = GameContext.Load(session.Store, gameId, session.Device);
            result = ManualSaves.SaveCurrent(ctx, card.Label, updateSlug: slug);
        }));
        if (!ok || result is null) return;
        LastSyncText = SyncText(result.Sync);
        await Shell.ReloadGameAsync(gameId);
        Shell.Notify($"Updated “{result.Save.Name}”: {result.KeysSaved} setting(s) — now active. {LastSyncText}", result.Sync is { Ok: false });
    }

    internal async Task<bool> RenameAsync(PresetCardViewModel card, string newName)
    {
        if (card.Slug is not { } slug || string.IsNullOrWhiteSpace(newName)) return false;
        var session = Session;
        var gameId = GameId;
        GitResult? git = null;
        var ok = await Shell.RunAsync("Renaming save", () => Task.Run(() =>
            git = ManualSaves.Rename(session.Store, gameId, session.Device.Name, slug, newName)));
        if (!ok) return false;
        LastSyncText = SyncText(git);
        await Shell.ReloadGameAsync(gameId);
        Shell.Notify($"Renamed to “{newName.Trim()}”. {LastSyncText}", git is { Ok: false });
        return true;
    }

    internal async Task DeleteAsync(PresetCardViewModel card)
    {
        if (card.Slug is not { } slug) return;
        var session = Session;
        var gameId = GameId;
        GitResult? git = null;
        var ok = await Shell.RunAsync($"Deleting “{card.Label}”", () => Task.Run(() =>
        {
            session.Store.LoadSave(gameId, session.Device.Name, slug); // throws if already gone
            git = ManualSaves.Delete(session.Store, gameId, session.Device.Name, slug);
        }));
        if (!ok) return;
        LastSyncText = SyncText(git);
        await Shell.ReloadGameAsync(gameId);
        Shell.Notify($"Deleted “{card.Label}”. {LastSyncText}", git is { Ok: false });
    }

    internal async Task LaunchAsync()
    {
        if (_snap.Game is not { } game) return;
        string? message = null;
        var ok = await Shell.RunAsync($"Launching {game.Name}", () => Task.Run(() => message = GameLauncher.Launch(game)));
        if (ok && message is not null) Shell.Notify(message);
    }

    private static string SyncText(GitResult? git) =>
        git is null ? "Git: not synced (AutoSync off or config isn't a git repo)." :
        git.Ok ? $"Git: {git.Message}." : $"Git sync failed: {git.Message}";
}
