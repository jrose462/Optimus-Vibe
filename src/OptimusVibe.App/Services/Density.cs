using System.Windows;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>One selectable layout density and the line its Settings card shows.</summary>
public sealed record DensityInfo(string Id, string Name, string Blurb);

/// <summary>
/// Layout density. Comfortable is the desktop layout; compact is for short windows (the ROG Xbox Ally X: 7" 1920x1080 at 150% is about
/// 1280x720 DIPs). Two things change together: the Density.*.xaml token dictionary is swapped in Application.Resources (spacing,
/// radii, font sizes, control heights, like the palettes) and <see cref="IsCompactProperty"/> (inherited) is set on every window so
/// styles and templates can switch layout with a plain Trigger. Auto re-evaluates on every resize / DPI change. UI thread only.
/// </summary>
public static class Density
{
    public const string Auto = "auto";
    public const string Compact = "compact";
    public const string Comfortable = "comfortable";

    /// <summary>Auto turns compact on below this window height (DIPs).</summary>
    public const double CompactBelowHeight = 800;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(Density),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsCompact(DependencyObject d) => (bool)d.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject d, bool value) => d.SetValue(IsCompactProperty, value);

    public static IReadOnlyList<DensityInfo> Modes { get; } =
    [
        new(Auto, "Auto", "Compact on small screens, roomy on the desktop"),
        new(Compact, "Compact", "Tighter spacing, more on screen"),
        new(Comfortable, "Comfortable", "Large cards and spacing"),
    ];

    public static string Mode { get; private set; } = Auto;

    /// <summary>The density currently applied.</summary>
    public static bool IsCompactNow { get; private set; }

    /// <summary>Raised on the UI thread when the applied density flips.</summary>
    public static event Action<bool>? Changed;

    public static string Normalize(string? id) =>
        Modes.FirstOrDefault(m => m.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? Auto;

    public static void SetMode(string? id) => Mode = Normalize(id);

    /// <summary>Compact for this window height under the current mode.</summary>
    public static bool Resolve(string mode, double windowHeight) => mode switch
    {
        Compact => true,
        Comfortable => false,
        _ => windowHeight > 0 && windowHeight < CompactBelowHeight,
    };

    /// <summary>Hook a window: evaluate now and whenever it is resized or moved to another DPI.</summary>
    public static void Attach(Window window)
    {
        window.SizeChanged += (_, _) => Apply(window);
        window.DpiChanged += (_, _) => Apply(window);
        window.StateChanged += (_, _) => Apply(window);
        Apply(window);
    }

    /// <summary>Re-evaluate for <paramref name="window"/> (also after the mode was changed in Settings).</summary>
    public static void Apply(Window window)
    {
        double h = window.ActualHeight;
        if (h <= 0) h = window.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Height : window.Height;
        bool compact = Resolve(Mode, h);
        SetIsCompact(window, compact);
        if (compact == IsCompactNow && Loaded) return;
        IsCompactNow = compact;
        Loaded = true;
        SwapDictionary(compact);
        foreach (Window w in Application.Current.Windows) SetIsCompact(w, compact);
        Log.Info($"Density: {(compact ? Compact : Comfortable)} (mode {Mode}, window {h:0} DIPs high)");
        Changed?.Invoke(compact);
    }

    private static bool Loaded;

    private static Uri DictionaryUri(bool compact) =>
        new($"pack://application:,,,/OptimusVibe;component/Themes/Density.{(compact ? "Compact" : "Comfortable")}.xaml", UriKind.Absolute);

    private static void SwapDictionary(bool compact)
    {
        var app = Application.Current;
        if (app is null) return;
        try
        {
            var merged = app.Resources.MergedDictionaries;
            var next = new ResourceDictionary { Source = DictionaryUri(compact) };
            int at = merged.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Density.", StringComparison.OrdinalIgnoreCase) == true);
            if (at >= 0) merged[at] = next; else merged.Insert(0, next);
        }
        catch (Exception ex)
        {
            Log.Error("Applying the density failed", ex);
        }
    }
}
