using System.Windows.Input;
using System.Windows.Media;
using GameProfiles.App.Services;
using GameProfiles.Library;

namespace GameProfiles.App.ViewModels;

public enum TileStatus { Managed, NeedsPresets, OptedOut, NotInstalled, Error }

/// <summary>One cover tile in the library grid (and the header of its detail page).</summary>
public sealed class LibraryTileViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;
    private LibraryEntry _entry;
    private GameItemViewModel? _gameItem;
    private ImageSource? _art;

    public LibraryTileViewModel(LibraryViewModel owner, LibraryEntry entry, GameItemViewModel? gameItem)
    {
        _owner = owner;
        _entry = entry;
        _gameItem = gameItem;
        Fallback = CoverArt.Fallback(Store);

        bool Idle() => !owner.Shell.IsBusy;
        PlayCommand = new AsyncRelayCommand(() => owner.PlayAsync(this), () => Idle() && CanPlay);
        OpenCommand = new RelayCommand(() => owner.OpenDetail(this));
        SetManagedCommand = new AsyncRelayCommand(p => owner.SetManagedAsync(this, p is true), _ => Idle() && CanToggleManaged);
    }

    public LibraryEntry Entry => _entry;
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

    public string StatusIcon => Status switch
    {
        TileStatus.Managed => "",       // check
        TileStatus.NeedsPresets => "",  // stopwatch
        TileStatus.OptedOut => "",      // blocked-ish
        TileStatus.Error => "",         // error
        _ => "",                        // download
    };

    public bool IsInstalled => _entry.Detected is not null;
    public bool HasConfig => _entry.Game is not null;

    /// <summary>"Manage this game's settings" — on unless opted out.</summary>
    public bool IsManaged => !_entry.OptedOut && _entry.Status != LibraryStatus.OptedOut;

    public bool CanToggleManaged => IsInstalled || HasConfig;
    public bool SuggestOptOut => _entry.SuggestOptOut;
    public bool IsQueued => _entry.Status == LibraryStatus.NeedsPresets && IsManaged;
    public bool CanPlay => GameLauncher.CanLaunch(_entry.Game, _entry.Detected);

    public string PlayHint => Status switch
    {
        TileStatus.Managed => "Your preset is applied, then restored after you quit",
        TileStatus.NeedsPresets => "Starts normally — presets aren't ready yet",
        TileStatus.OptedOut => "Starts normally — GameProfiles leaves its settings alone",
        _ => "",
    };

    // ---------------------------------------------------------------- art

    public ImageSource? Art { get => _art; private set { if (Set(ref _art, value)) OnPropertyChanged(nameof(HasArt)); } }
    public bool HasArt => _art is not null;
    public string? ArtPath => _entry.Detected?.ArtPath;
    public Brush Fallback { get; }
    public string Glyph => CoverArt.Glyph(Store);
    public string Initials => CoverArt.Initials(Name);

    public void SetArt(ImageSource? img) => Art = img;

    public ICommand PlayCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SetManagedCommand { get; }

    public void Update(LibraryEntry entry, GameItemViewModel? gameItem)
    {
        _entry = entry;
        _gameItem = gameItem;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-read the game list badge (after Use / Apply in the detail page).</summary>
    public void RefreshBadge() => OnPropertyChanged(nameof(StatusText));

    public override string ToString() => $"{Name}, {StatusText}"; // automation name
}
