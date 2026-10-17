using System.Windows;
using System.Windows.Media;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>One selectable colour theme and the brushes its picker card previews.</summary>
public sealed record ThemeInfo(string Id, string Name, string Blurb);

/// <summary>
/// Colour themes: each is a palette dictionary (Themes\Palette.*.xaml) with the same keys. Styles use DynamicResource, so
/// swapping the active palette dictionary in Application.Resources restyles the open window instantly, no restart.
/// UI thread only.
/// </summary>
public static class ThemeManager
{
    public const string Default = "default";

    public static IReadOnlyList<ThemeInfo> Themes { get; } =
    [
        new(Default, "Default", "Navy and teal, one calm blue"),
        new("majestic", "Majestic", "Black and gold"),
        new("circuit", "Circuit", "Blue-black with cyan and green"),
    ];

    private static readonly Dictionary<string, ResourceDictionary> Cache = new();

    public static string Current { get; private set; } = Default;

    /// <summary>Unknown / empty names fall back to the default theme.</summary>
    public static string Normalize(string? id) =>
        Themes.FirstOrDefault(t => t.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? Default;

    private static Uri PaletteUri(string id) =>
        new($"pack://application:,,,/OptimusVibe;component/Themes/Palette.{char.ToUpperInvariant(id[0])}{id[1..]}.xaml", UriKind.Absolute);

    /// <summary>The palette's dictionary (cached, never merged into the app): used to preview a theme without applying it.</summary>
    public static ResourceDictionary Palette(string id)
    {
        id = Normalize(id);
        if (!Cache.TryGetValue(id, out var dict))
            Cache[id] = dict = new ResourceDictionary { Source = PaletteUri(id) };
        return dict;
    }

    public static Brush? PreviewBrush(string id, string key)
    {
        try
        {
            if (Palette(id)[key] is Brush b)
            {
                var copy = (Brush)b.CloneCurrentValue();
                if (copy.CanFreeze) copy.Freeze();
                return copy;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Theme preview {id}/{key}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Make <paramref name="id"/> the active palette. Never throws: on failure the current theme stays.</summary>
    public static void Apply(string? id)
    {
        id = Normalize(id);
        var app = Application.Current;
        if (app is null) return;
        try
        {
            var merged = app.Resources.MergedDictionaries;
            var next = new ResourceDictionary { Source = PaletteUri(id) };
            int at = merged.ToList().FindIndex(d => d.Source?.OriginalString.Contains("/Palette.", StringComparison.OrdinalIgnoreCase) == true
                                                    || d.Source?.OriginalString.StartsWith("Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true);
            if (at >= 0) merged[at] = next; else merged.Insert(0, next);
            Current = id;
            Log.Info($"Theme: {id}");
        }
        catch (Exception ex)
        {
            Log.Error($"Applying theme '{id}' failed", ex);
        }
    }
}
