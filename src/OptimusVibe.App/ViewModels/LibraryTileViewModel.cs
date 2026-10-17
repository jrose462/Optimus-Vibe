using System.Windows.Input;
using System.Windows.Media;
using OptimusVibe.App.Services;
using OptimusVibe.CloudSync;
using OptimusVibe.GameLab;
using OptimusVibe.Library;

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
            _entry.Status == LibraryStatus.NeedsPresets));
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

    /// <summary>Chip text: the active preset ("Performance", "Manual: Steady 40") or the status.</summary>
    public string StatusText => Status switch
    {
        TileStatus.Managed => _gameItem?.BadgeText is { Length: > 0 } b ? b : "Managed",
        TileStatus.NeedsPresets => "Needs presets",
        TileStatus.OptedOut => "Opted out",
        TileStatus.Error => "Config error",
        _ => "Not installed",
    };

    /// <summary>One short muted line under the title on a tile.</summary>
    public string StatusLine => Status switch
    {
        TileStatus.Managed => _gameItem?.BadgeText is { Length: > 0 } b
            ? (b.StartsWith("Manual", StringComparison.OrdinalIgnoreCase) ? b : b + " preset")
            : "Tuned",
        TileStatus.NeedsPresets => "Needs presets",
        TileStatus.OptedOut => "Opted out",
        TileStatus.Error => "Config problem",
        _ => "Not installed",
    };

    /// <summary>Small orange dot: this game needs attention (waiting for presets, or a config problem).</summary>
    public bool NeedsAttention => Status is TileStatus.NeedsPresets or TileStatus.Error;

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

    public bool CanPlay => GameLauncher.CanLaunch(_entry.Game, _entry.Detected);

    public string PlayHint => Status switch
    {
        TileStatus.Managed => "Your preset is applied, then restored after you quit",
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
        OnPropertyChanged(string.Empty);
        Lab.Refresh();
    }

    /// <summary>Re-read the game list badge (after Use / Apply in the detail page).</summary>
    public void RefreshBadge()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusLine));
    }

    public override string ToString() => $"{Name}, {StatusText}"; // automation name
}
