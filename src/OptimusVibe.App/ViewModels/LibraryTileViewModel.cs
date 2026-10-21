using System.Windows.Input;
using System.Windows.Media;
using OptimusVibe.App.Services;
using OptimusVibe.CloudSync;
using OptimusVibe.GameLab;
using OptimusVibe.Library;
using OptimusVibe.Logging;

namespace OptimusVibe.App.ViewModels;

public enum TileStatus { Managed, NeedsPresets, OptedOut, NotInstalled, Error }

/// <summary>One cover tile in the library grid (and the header of its detail page).</summary>
public sealed class LibraryTileViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;
    private LibraryEntry _entry;
    private GameItemViewModel? _gameItem;
    private ImageSource? _art;
    private ImageSource? _icon;
    private bool _healthLoaded;
    private GameHealth? _health;
    private (string? Build, ProbeRecord? Mapping) _install;
    private int _installRead;

    public LibraryTileViewModel(LibraryViewModel owner, LibraryEntry entry, GameItemViewModel? gameItem)
    {
        _owner = owner;
        _entry = entry;
        _gameItem = gameItem;

        bool Idle() => !owner.Shell.IsBusy;
        PlayCommand = new AsyncRelayCommand(() => owner.PlayAsync(this), () => Idle() && CanPlay);
        OpenCommand = new RelayCommand(() => owner.OpenDetail(this));
        SetManagedCommand = new AsyncRelayCommand(p => owner.SetManagedAsync(this, p is true), _ => Idle() && CanToggleManaged);
        SetSteamKeepCommand = new AsyncRelayCommand(p => owner.SetSteamKeepUpToDateAsync(this, p is true), _ => Idle() && IsSteam);
        Lab = owner.Shell.Lab.ForGame(() => new LabGameContext(Name, Key, GameId, _entry.Detected is null ? null : LabTarget.From(_entry), HasConfig,
            _entry.Status == LibraryStatus.NeedsPresets, _gameItem?.BadgeText, _gameItem?.Snapshot.CloudSync, _gameItem?.Snapshot.Context));
        _ = ReadInstallAsync();
    }

    public LibraryEntry Entry => _entry;

    /// <summary>Game-lab actions and status for this game.</summary>
    public LabGameViewModel Lab { get; }
    public GameItemViewModel? GameItem => _gameItem;
    public string Key => _entry.Key;
    public string Name => _entry.Name;
    public string? GameId => _entry.Game?.Id;
    public string? Store => _entry.Detected?.Store ?? _entry.Game?.Store;
    public string StoreText => GameItemViewModel.StoreLabel(Store);

    public TileStatus Status => _entry.Status switch
    {
        LibraryStatus.Managed when _gameItem?.HasError == true => TileStatus.Error,
        LibraryStatus.Managed => TileStatus.Managed,
        LibraryStatus.NeedsPresets => TileStatus.NeedsPresets,
        LibraryStatus.OptedOut => TileStatus.OptedOut,
        _ => TileStatus.NotInstalled,
    };

    /// <summary>A managed game flagged "set in-game": it stays under the Managed filter, but its settings are left to the game.</summary>
    public bool IsSetInGame => Status == TileStatus.Managed && _entry.SetInGame is not null;

    /// <summary>Chip text: the active preset ("Performance", "Manual: Steady 40") or the status.</summary>
    public string StatusText => Status switch
    {
        TileStatus.Managed when IsSetInGame => "Set in-game",
        TileStatus.Managed => _gameItem?.BadgeText is { Length: > 0 } b ? b : "Managed",
        TileStatus.NeedsPresets => "Needs presets",
        TileStatus.OptedOut => "Opted out",
        TileStatus.Error => "Config error",
        _ => "Not installed",
    };

    /// <summary>One short muted line under the title on a tile.</summary>
    public string StatusLine => Status switch
    {
        TileStatus.Managed when IsSetInGame => "Set in-game",
        TileStatus.Managed => _gameItem?.BadgeText is { Length: > 0 } b
            ? (b.StartsWith("Manual", StringComparison.OrdinalIgnoreCase) ? b : b + " preset")
            : "Tuned",
        TileStatus.NeedsPresets => "Needs presets",
        TileStatus.OptedOut => "Opted out",
        TileStatus.Error => "Config problem",
        _ => "Not installed",
    };

    /// <summary>Small orange dot: this game needs attention (waiting for presets, or a config problem). A health state has its chip instead.</summary>
    public bool NeedsAttention => Status is TileStatus.NeedsPresets or TileStatus.Error;

    // ---------------------------------------------------------------- health chip

    /// <summary>
    /// The game's health on this device for the tile (<see cref="GameHealth.Evaluate"/> over the inputs its snapshot read once per load, plus the
    /// installed build and stored mapping run, as the game page reads them), for managed games that are not flagged. Null otherwise. Worked out
    /// once per update, without touching the disk (those two are read off the UI thread by <see cref="ReadInstallAsync"/>).
    /// </summary>
    public GameHealth? Health
    {
        get
        {
            if (_healthLoaded) return _health;
            _healthLoaded = true;
            _health = null;
            if (!WantsHealth || _gameItem?.Snapshot.Health is not { } inputs) return null;
            try
            {
                _health = GameHealth.Evaluate(inputs.WithInstall(_install.Build, _install.Mapping) with { CanLaunch = CanPlay, LabReady = _owner.Shell.Lab.IsReady });
            }
            catch (Exception ex)
            {
                Log.Warn($"{Name}: could not work out its health: {ex.Message}");
            }
            return _health;
        }
    }

    private bool WantsHealth => Status == TileStatus.Managed && !IsSetInGame;

    /// <summary>
    /// Reads the installed build (a directory scan for games without a store build id) and the stored mapping run off the UI thread, then
    /// re-works the chip. The last values stay in use meanwhile; a newer read wins over an older one. Never throws.
    /// </summary>
    private async Task ReadInstallAsync()
    {
        if (!WantsHealth || _gameItem?.Snapshot.Health is not { } inputs || _owner.Shell.Session is not { } session) return;
        var read = ++_installRead;
        var detected = _entry.Detected;
        var target = detected is null ? null : LabTarget.From(_entry);
        try
        {
            var install = await Task.Run(() => (
                // the build is only worth reading when something recorded the build the presets worked on
                Build: inputs.BaselineBuild is not null && detected is not null ? GameBuild.Of(detected) : null,
                Mapping: target is null ? null : GameLabService.StoredMapping(session, target)));
            if (read != _installRead || install == _install) return;
            _install = install;
            RefreshBadge(readInstall: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"{Name}: could not read its installed build: {ex.Message}");
        }
    }

    /// <summary>"Game updated" / "Changed by game" (<see cref="GameHealth.TileChip"/>); null when the game needs no attention.</summary>
    public string? AttentionChip => Health?.TileChip;

    public bool HasAttentionChip => AttentionChip is not null;

    public bool IsInstalled => _entry.Detected is not null;
    public bool HasConfig => _entry.Game is not null;

    /// <summary>"Manage this game's settings" — on unless opted out.</summary>
    public bool IsManaged => !_entry.OptedOut && _entry.Status != LibraryStatus.OptedOut;

    public bool CanToggleManaged => IsInstalled || HasConfig;

    /// <summary>Steam games only: "Keep Steam launch option up to date" (off = steamLaunchOptionsSkip).</summary>
    public bool IsSteam => Key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase);
    public bool KeepSteamOptionUpToDate => !_entry.SteamLaunchOptionSkipped;
    public bool SuggestOptOut => _entry.SuggestOptOut;
    public bool IsQueued => _entry.Status == LibraryStatus.NeedsPresets && IsManaged;
    // ---------------------------------------------------------------- settings sync

    /// <summary>Do this game's settings travel between devices? Stays on device / Syncs / Maybe (unknown, low confidence, no report).</summary>
    public SyncBucket SyncBucket => SyncBuckets.Classify(_entry.CloudSync);

    public string SyncGlyph => SyncBucket switch
    {
        SyncBucket.Syncs => "\uE753",          // cloud
        SyncBucket.StaysOnDevice => "\uE7F8",  // device
        _ => "\uE897",                         // question mark
    };

    /// <summary>The cloud-sync report's headline ("Settings stay on this device"), or "Not known yet".</summary>
    public string SyncTip => _entry.CloudSync?.Headline ?? "Not known yet";

    /// <summary>Bucket plus confidence for a list row: "Syncs between devices · high confidence", "Maybe syncs · not known yet".</summary>
    public string SyncLabel => SyncLabelFor(SyncBucket, _entry.CloudSync);

    public static string SyncLabelFor(SyncBucket bucket, CloudSyncReport? report)
    {
        var what = bucket switch
        {
            SyncBucket.Syncs => "Syncs between devices",
            SyncBucket.StaysOnDevice => "Stays on this device",
            _ => "Maybe syncs",
        };
        return $"{what} · {SyncBuckets.ConfidenceLabel(report)}";
    }

    /// <summary>The sync bucket rests on what was seen on this device (medium+ detection), not only research.</summary>
    public bool SyncConfirmed => SyncBuckets.IsConfirmed(_entry.CloudSync);

    public bool CanPlay => GameLauncher.CanLaunch(_entry.Game, _entry.Detected);

    public string PlayHint => Status switch
    {
        TileStatus.Managed when IsSetInGame => "Display and graphics-driver settings are applied; the game's own settings are left to the game",
        TileStatus.Managed =>"Your preset is applied, then restored after you quit",
        TileStatus.NeedsPresets => "Starts normally — presets aren't ready yet",
        TileStatus.OptedOut => "Starts normally — Optimus-Vibe leaves its settings alone",
        _ => "",
    };

    // ---------------------------------------------------------------- art

    public ImageSource? Art { get => _art; private set { if (Set(ref _art, value)) OnPropertyChanged(nameof(HasArt)); } }
    public bool HasArt => _art is not null;
    public string? ArtPath => _entry.Detected?.ArtPath;
    public ImageSource? Icon { get => _icon; private set { if (Set(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => _icon is not null;
    public DetectedGame? Detected => _entry.Detected;

    public void SetArt(ImageSource? img) => Art = img;
    public void SetIcon(ImageSource? img) => Icon = img;

    public ICommand PlayCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SetManagedCommand { get; }
    public ICommand SetSteamKeepCommand { get; }

    public void Update(LibraryEntry entry, GameItemViewModel? gameItem)
    {
        _entry = entry;
        _gameItem = gameItem;
        _healthLoaded = false;
        OnPropertyChanged(string.Empty);
        Lab.Refresh();
        _ = ReadInstallAsync();
    }

    /// <summary>Re-read the game list badge (after Use / Apply in the detail page, or a reload of the game after a session).</summary>
    public void RefreshBadge() => RefreshBadge(readInstall: true);

    private void RefreshBadge(bool readInstall)
    {
        if (readInstall) _ = ReadInstallAsync();
        _healthLoaded = false;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(AttentionChip));
        OnPropertyChanged(nameof(HasAttentionChip));
    }

    // automation name: the chip is part of it, so it needs no focus stop of its own
    public override string ToString() => AttentionChip is { } chip ? $"{Name}, {StatusText}, {chip}" : $"{Name}, {StatusText}";
}
