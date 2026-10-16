using GameProfiles.App.Services;
using GameProfiles.Config;

namespace GameProfiles.App.ViewModels;

public enum BadgeKind { None, Recommended, Manual, Error }

/// <summary>One row in the game list.</summary>
public sealed class GameItemViewModel(GameSnapshot snapshot) : ObservableObject
{
    private GameSnapshot _snapshot = snapshot;
    private GameDetailViewModel? _detail;

    public GameSnapshot Snapshot => _snapshot;
    public string Id => _snapshot.Id;
    public string Name => _snapshot.Name;
    public bool HasError => _snapshot.Error is not null;
    public string StoreText => StoreLabel(_snapshot.Game?.Store);

    public BadgeKind BadgeKind
    {
        get
        {
            var ctx = _snapshot.Context;
            if (ctx is null) return BadgeKind.Error;
            if (ctx.Profile.IsEmpty) return BadgeKind.None;
            return ctx.Preset?.Kind == PresetKind.Manual ? BadgeKind.Manual : BadgeKind.Recommended;
        }
    }

    /// <summary>e.g. "Performance" or "Manual: Steady 40".</summary>
    public string BadgeText
    {
        get
        {
            var ctx = _snapshot.Context;
            if (ctx is null) return "Config error";
            if (ctx.Profile.IsEmpty) return "No presets";
            if (ctx.Preset?.Kind == PresetKind.Manual) return $"Manual: {ctx.Save?.Name ?? ctx.Preset.Name}";
            return ctx.Profile.Label ?? ctx.Preset?.Name ?? "";
        }
    }

    /// <summary>"device default" when nothing has been picked for this game yet.</summary>
    public string? BadgeSubText => _snapshot.Context is { IsSelected: false, Profile.IsEmpty: false } ? "device default" : null;

    /// <summary>Muted line under the name in the list.</summary>
    public string StatusLine => BadgeKind switch
    {
        BadgeKind.Error => "Config problem",
        BadgeKind.None => "No presets for this device",
        BadgeKind.Manual => BadgeText,
        _ => BadgeText + " preset" + (BadgeSubText is null ? "" : " \u00b7 device default"),
    };

    public bool NeedsAttention => BadgeKind is BadgeKind.Error or BadgeKind.None;

    public GameDetailViewModel? Detail => _detail;

    public void EnsureDetail(IAppShell shell) => _detail ??= new GameDetailViewModel(shell, _snapshot);

    public void Update(GameSnapshot snapshot)
    {
        _snapshot = snapshot;
        _detail?.Update(snapshot);
        OnPropertyChanged(string.Empty);
    }

    public override string ToString() => Name; // list item automation name / screen readers

    public static string StoreLabel(string? store) => (store ?? "").ToLowerInvariant() switch
    {
        "steam" => "Steam",
        "battlenet" => "Battle.net",
        "ea" => "EA app",
        "epic" => "Epic",
        "gog" => "GOG",
        "xbox" => "Xbox",
        "" => "",
        var other => other,
    };
}
