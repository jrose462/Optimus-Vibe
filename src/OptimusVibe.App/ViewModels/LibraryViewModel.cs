using System.Collections.ObjectModel;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.CloudSync;
using OptimusVibe.Config;
using OptimusVibe.Library;
using OptimusVibe.Logging;

namespace OptimusVibe.App.ViewModels;

public enum LibraryFilter { All, Managed, NeedsPresets, OptedOut }

/// <summary>The second filter row: do the game's settings travel between devices? (Combines with <see cref="LibraryFilter"/>.)</summary>
public enum SyncFilter { All, StaysOnDevice, Syncs, Maybe }

public sealed class SyncPillViewModel(SyncFilter filter, string label, string glyph, string hint) : ObservableObject
{
    private bool _isSelected;
    private int _count;

    public SyncFilter Filter { get; } = filter;
    public string Label { get; } = label;
    public string Glyph { get; } = glyph;
    public string Hint { get; } = hint;
    public int Count { get => _count; set { if (Set(ref _count, value)) { OnPropertyChanged(nameof(ToolTipText)); OnPropertyChanged(nameof(AutomationName)); } } }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string ToolTipText => $"{Hint} · {Count}";
    public string AutomationName => $"{Label}, {Count}";
}

public sealed class FilterPillViewModel(LibraryFilter filter, string label) : ObservableObject
{
    /// <summary>Segoe Fluent icon for the segmented filter strip.</summary>
    public string Glyph { get; } = filter switch
    {
        LibraryFilter.Managed => "\uE73E",
        LibraryFilter.NeedsPresets => "\uE916",
        LibraryFilter.OptedOut => "\uE711",
        _ => "\uE80F",
    };
    public string ToolTipText => Count > 0 || Filter == LibraryFilter.All ? $"{Label} · {Count}" : Label;

    private bool _isSelected;
    private int _count;

    public LibraryFilter Filter { get; } = filter;
    public string Label { get; } = label;
    public int Count { get => _count; set { if (Set(ref _count, value)) { OnPropertyChanged(nameof(ToolTipText)); OnPropertyChanged(nameof(AutomationName)); } } }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string AutomationName => $"{Label}, {Count}";
}

/// <summary>
/// The controller-first library: every installed game (plus configured ones not installed here) as cover tiles,
/// filterable by status. Scans launchers in the background; never takes the busy gate for the scan itself.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, LibraryTileViewModel> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DetectedGame>? _detected;
    private LibraryFilter _filter = LibraryFilter.All;
    private SyncFilter _syncFilter = SyncFilter.All;
    private LibraryTileViewModel? _selectedTile;
    private bool _isScanning;
    private bool _hasScanned;
    private int _pendingRequests;
    private int _scanGeneration;

    public LibraryViewModel(MainViewModel main)
    {
        _main = main;
        Filters =
        [
            new(LibraryFilter.All, "All"),
            new(LibraryFilter.Managed, "Tuned"),
            new(LibraryFilter.NeedsPresets, "Needs presets"),
            new(LibraryFilter.OptedOut, "Opted out"),
        ];
        Filters[0].IsSelected = true;
        SyncFilters =
        [
            new(SyncFilter.All, "All", "\uE80F", "Every game, whether or not its settings sync"),
            new(SyncFilter.StaysOnDevice, "Stays on device", "\uE7F8", "Settings stay on each device"),
            new(SyncFilter.Syncs, "Syncs", "\uE753", "Settings sync between your devices"),
            new(SyncFilter.Maybe, "Maybe", "\uE897", "Not known yet"),
        ];
        SyncFilters[0].IsSelected = true;
        SetSyncFilterCommand = new RelayCommand(p => { if (p is SyncPillViewModel f) SetSyncFilter(f.Filter); });
        SetFilterCommand = new RelayCommand(p => { if (p is FilterPillViewModel f) SetFilter(f.Filter); });
        RescanCommand = new AsyncRelayCommand(() => ScanAsync(rescanLaunchers: true), () => !IsScanning && main.Session is not null);
        OpenQueueCommand = new RelayCommand(() => { if (main.Lab.IsReady) _ = main.Lab.OpenQueueAsync(); else main.OpenSettings("gamelab"); });
    }

    public IAppShell Shell => _main;

    public ObservableCollection<LibraryTileViewModel> Tiles { get; } = new();
    public ObservableCollection<FilterPillViewModel> Filters { get; }
    public ObservableCollection<SyncPillViewModel> SyncFilters { get; }

    public LibraryTileViewModel? SelectedTile { get => _selectedTile; set => Set(ref _selectedTile, value); }

    public LibraryFilter Filter => _filter;
    public SyncFilter SyncFilter => _syncFilter;

    public bool IsScanning
    {
        get => _isScanning;
        private set { if (Set(ref _isScanning, value)) { OnPropertyChanged(nameof(ShowEmpty)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool HasScanned { get => _hasScanned; private set { if (Set(ref _hasScanned, value)) { OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(Lead)); } } }

    public bool ShowEmpty => Tiles.Count == 0 && !IsScanning && HasScanned;

    public string EmptyText => _syncFilter != SyncFilter.All
        ? "No games match this settings-sync filter. Pick All to see everything."
        : _filter switch
    {
        LibraryFilter.Managed => "Nothing is tuned yet. Games get presets once Claude has processed the queue.",
        LibraryFilter.NeedsPresets => "Every installed game already has presets for this device.",
        LibraryFilter.OptedOut => "You haven't opted any game out.",
        _ => "No games found. Check which launchers are scanned in Settings, or add a game with Claude.",
    };

    public IReadOnlyList<DetectedGame> Detected => _detected ?? [];

    // ---------------------------------------------------------------- summary ("This device")

    public int TunedCount => Filters[1].Count;
    public int WaitingCount => Filters[2].Count;
    public int OptedOutCount => Filters[3].Count;
    public string DeviceFriendly => _main.DeviceFriendly;
    public string DeviceName => _main.DeviceName;

    /// <summary>The one friendly sentence under the page title.</summary>
    public string Lead
    {
        get
        {
            var dev = _main.DeviceFriendly;
            if (!HasScanned) return $"Looking for the games installed on this {dev}\u2026";
            if (TunedCount == 0) return WaitingCount > 0
                ? $"Nothing is tuned for this {dev} yet \u2014 {WaitingCount} {(WaitingCount == 1 ? "game is" : "games are")} waiting for presets."
                : $"Nothing is tuned for this {dev} yet.";
            return $"Ready to play. {TunedCount} {(TunedCount == 1 ? "game is" : "games are")} tuned for this {dev}.";
        }
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(TunedCount));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(OptedOutCount));
        OnPropertyChanged(nameof(DeviceFriendly));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(Lead));
    }

    /// <summary>Requests waiting in config/requests.yaml for Claude.</summary>
    public int PendingRequests
    {
        get => _pendingRequests;
        private set { if (Set(ref _pendingRequests, value)) { OnPropertyChanged(nameof(QueuedNote)); OnPropertyChanged(nameof(HasQueuedNote)); } }
    }

    public bool HasQueuedNote => PendingRequests > 0;
    public string QueuedNote => PendingRequests == 1 ? "1 game queued for Claude" : $"{PendingRequests} games queued for Claude";

    public ICommand SetFilterCommand { get; }
    public ICommand SetSyncFilterCommand { get; }
    public ICommand RescanCommand { get; }
    /// <summary>The "N games queued" note: opens the game lab board (or Settings when the lab is not set up).</summary>
    public ICommand OpenQueueCommand { get; }

    // ---------------------------------------------------------------- filter

    public void NextFilter() => SetFilter((LibraryFilter)(((int)_filter + 1) % Filters.Count));
    public void PreviousFilter() => SetFilter((LibraryFilter)(((int)_filter + Filters.Count - 1) % Filters.Count));

    public void SetFilter(LibraryFilter filter)
    {
        _filter = filter;
        foreach (var f in Filters) f.IsSelected = f.Filter == filter;
        ApplyFilter();
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(EmptyText));
    }

    public void SetSyncFilter(SyncFilter filter)
    {
        _syncFilter = filter;
        foreach (var f in SyncFilters) f.IsSelected = f.Filter == filter;
        ApplyFilter();
        OnPropertyChanged(nameof(SyncFilter));
        OnPropertyChanged(nameof(EmptyText));
    }

    private static bool MatchesStatus(LibraryFilter filter, LibraryTileViewModel t) => filter switch
    {
        LibraryFilter.Managed => t.Status is TileStatus.Managed or TileStatus.Error,
        LibraryFilter.NeedsPresets => t.Status == TileStatus.NeedsPresets,
        LibraryFilter.OptedOut => t.Status == TileStatus.OptedOut,
        _ => true,
    };

    /// <summary>The "Settings sync" row: <see cref="SyncBuckets.Classify"/> of the tile's cloud-sync report.</summary>
    public static bool MatchesSync(SyncFilter filter, SyncBucket bucket) => filter switch
    {
        SyncFilter.StaysOnDevice => bucket == SyncBucket.StaysOnDevice,
        SyncFilter.Syncs => bucket == SyncBucket.Syncs,
        SyncFilter.Maybe => bucket == SyncBucket.Maybe,
        _ => true,
    };

    private bool Matches(LibraryTileViewModel t) => MatchesStatus(_filter, t) && MatchesSync(_syncFilter, t.SyncBucket);

    private static int Order(LibraryTileViewModel t) => t.IsInstalled ? 0 : 1; // not-installed configs go last

    /// <summary>Rebuild the visible list in place (keeps containers, and therefore focus, where possible).</summary>
    private void ApplyFilter()
    {
        var keep = SelectedTile;
        var want = _byKey.Values.Where(Matches)
            .OrderBy(Order).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = Tiles.Count - 1; i >= 0; i--)
            if (!want.Contains(Tiles[i])) Tiles.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            var at = Tiles.IndexOf(want[i]);
            if (at < 0) Tiles.Insert(i, want[i]);
            else if (at != i) Tiles.Move(at, i);
        }
        // Status pills count independent of the sync row (they feed the "This device" numbers); sync pills count within the status filter.
        foreach (var f in Filters) f.Count = _byKey.Values.Count(t => MatchesStatus(f.Filter, t));
        foreach (var f in SyncFilters) f.Count = _byKey.Values.Count(t => MatchesStatus(_filter, t) && MatchesSync(f.Filter, t.SyncBucket));
        RaiseSummary();
        SelectedTile = keep is not null && Tiles.Contains(keep) ? keep : Tiles.FirstOrDefault();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    // ---------------------------------------------------------------- scanning

    /// <summary>
    /// Launcher scan (or reuse the last one) → library entries → request queue → Steam launch options. All Core work
    /// off the UI thread. Queued requests are synced when AutoSync is on.
    /// </summary>
    public async Task ScanAsync(bool rescanLaunchers)
    {
        var session = _main.Session;
        if (session is null) return;
        var gen = ++_scanGeneration;
        var games = _main.Games.Select(g => g.Snapshot).ToList();
        var detected = rescanLaunchers ? null : _detected;
        IsScanning = true;
        LibraryScanResult? result = null;
        var routeToLab = _main.Lab.RoutesToLab; // lab set up: new games go to the lab instead of the local requests.yaml queue
        try
        {
            result = await Task.Run(() => LibraryScan.Run(session, games, detected, queue: !routeToLab));
        }
        catch (Exception ex)
        {
            Log.Error("Library scan failed", ex);
            _main.Notify($"Library scan failed: {ex.Message}", error: true);
        }
        finally
        {
            if (gen == _scanGeneration) IsScanning = false;
        }
        if (result is null || gen != _scanGeneration || _main.Session != session) return;

        _detected = result.Detected;
        Apply(result.Entries);
        HasScanned = true;
        if (result.Pending is { } p) PendingRequests = p;
        else PendingRequests = result.Entries.Count(e => e.Status == LibraryStatus.NeedsPresets && !e.OptedOut);
        _ = LoadArtAsync();
        if (routeToLab)
            _ = _main.Lab.AutoRequestAsync(result.Entries.Where(e => e.Status == LibraryStatus.NeedsPresets && !e.OptedOut && e.Detected is not null).ToList());

        if (result.Warnings.Count > 0) _main.Notify(string.Join(" ", result.Warnings), error: true);
        if (result.SteamApplied > 0) _main.Notify($"Steam launch options updated for {result.SteamApplied} game(s).");
        if (result.SteamChanges.Count > 0) _main.OfferSteamChanges(result.SteamChanges);
        _main.OnLibraryChanged();

        if (result.Queued > 0)
        {
            var settings = await SettingsStore.LoadAsync();
            if (settings.AutoSync)
                await _main.SyncFilesQuietlyAsync($"chore(requests): queue {result.Queued} game(s) for presets", session.Paths.RequestsFile);
        }
    }

    private void Apply(IReadOnlyList<LibraryEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!seen.Add(e.Key)) continue;
            var item = e.Game is { } g
                ? _main.Games.FirstOrDefault(x => string.Equals(x.Id, g.Id, StringComparison.OrdinalIgnoreCase))
                : null;
            if (_byKey.TryGetValue(e.Key, out var tile)) tile.Update(e, item);
            else _byKey[e.Key] = new LibraryTileViewModel(this, e, item);
        }
        foreach (var gone in _byKey.Keys.Where(k => !seen.Contains(k)).ToList()) _byKey.Remove(gone);
        ApplyFilter();
    }

    private async Task LoadArtAsync()
    {
        var todo = _byKey.Values.Where(t => t.Art is null && t.ArtPath is not null).ToList();
        foreach (var t in todo)
        {
            var img = await Task.Run(() => CoverArt.Load(t.ArtPath));
            t.SetArt(img);
        }
        foreach (var t in _byKey.Values.Where(t => t.Art is null && t.Icon is null).ToList())
        {
            var det = t.Detected;
            var icon = await Task.Run(() => CoverArt.LoadExeIcon(det));
            t.SetIcon(icon);
        }
    }

    /// <summary>Badges follow Use / Apply / reloads in the detail page.</summary>
    public void RefreshBadges()
    {
        foreach (var t in _byKey.Values)
        {
            var item = t.GameId is { } id ? _main.Games.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) : null;
            if (item != t.GameItem) t.Update(t.Entry, item);
            else t.RefreshBadge();
        }
    }

    public void Clear()
    {
        _scanGeneration++;
        _byKey.Clear();
        Tiles.Clear();
        _detected = null;
        HasScanned = false;
        PendingRequests = 0;
    }

    public LibraryTileViewModel? FindByGameId(string gameId) =>
        _byKey.Values.FirstOrDefault(t => string.Equals(t.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Display name for a Steam app id: the library entry, else the game config with that steamAppId, else its game id.</summary>
    public string SteamGameName(int appId, string? gameId)
    {
        if (_byKey.TryGetValue($"steam:{appId}", out var tile)) return tile.Name;
        var item = _main.Games.FirstOrDefault(g => g.Snapshot.Game?.SteamAppId == appId);
        if (item is not null) return item.Name;
        if (!string.IsNullOrEmpty(gameId)) return gameId;
        return $"Steam app {appId}";
    }

    // ---------------------------------------------------------------- actions

    public void OpenDetail(LibraryTileViewModel tile) => _main.OpenLibraryDetail(tile);

    public Task PlayAsync(LibraryTileViewModel tile) => _main.PlayTileAsync(tile);

    /// <summary>
    /// "Keep Steam launch option up to date": write/remove the game's steamLaunchOptionsSkip entry, sync library.yaml when
    /// AutoSync is on, and re-plan (turning it on shows the Steam banner again when a change is needed).
    /// </summary>
    public async Task SetSteamKeepUpToDateAsync(LibraryTileViewModel tile, bool keep)
    {
        var session = _main.Session;
        if (session is null) return;
        var key = tile.Key;
        var ok = await _main.RunAsync(keep ? $"Updating {tile.Name}'s Steam launch option" : $"Leaving {tile.Name}'s Steam launch option alone", () => Task.Run(() =>
        {
            LibraryService.SetSteamLaunchOptionSkip(session.Store, key, !keep);
            Log.Info($"Library: {key} Steam launch option {(keep ? "managed" : "skipped")}");
        }));
        if (!ok)
        {
            tile.Update(tile.Entry, tile.GameItem); // revert the toggle
            return;
        }

        await ScanAsync(rescanLaunchers: false); // new entry flags + re-plan Steam launch options
        var settings = await SettingsStore.LoadAsync();
        if (settings.AutoSync)
            await _main.SyncFilesQuietlyAsync($"chore(library): {(keep ? "update" : "skip")} Steam launch option of {key}", session.Paths.LibraryFile);
        _main.Notify(keep
            ? $"{tile.Name}: its Steam launch option is kept up to date."
            : $"{tile.Name}: its Steam launch option stays as it is now.");
    }

    /// <summary>
    /// "Manage this game's settings": write the opt-out to config/library.yaml, fix the Steam launch option for
    /// this game, sync library.yaml when AutoSync is on, and rebuild the library from the last scan.
    /// </summary>
    public async Task SetManagedAsync(LibraryTileViewModel tile, bool managed)
    {
        var session = _main.Session;
        if (session is null) return;
        var key = tile.Key;
        var ok = await _main.RunAsync(managed ? $"Managing {tile.Name}" : $"Opting {tile.Name} out", () => Task.Run(() =>
        {
            LibraryService.SetOptOut(session.Store, key, !managed);
            Log.Info($"Library: {key} {(managed ? "managed" : "opted out")}");
        }));
        if (!ok)
        {
            tile.Update(tile.Entry, tile.GameItem); // revert the toggle
            return;
        }

        await ScanAsync(rescanLaunchers: false); // rebuild statuses, re-plan Steam launch options, re-queue
        var settings = await SettingsStore.LoadAsync();
        if (settings.AutoSync) await _main.SyncFilesQuietlyAsync($"chore(library): {(managed ? "manage" : "opt out of")} {key}", session.Paths.LibraryFile);
        _main.Notify(managed
            ? $"{tile.Name}: Optimus-Vibe manages its settings again."
            : $"{tile.Name}: opted out — it starts normally and Optimus-Vibe leaves its settings alone.");
    }
}
