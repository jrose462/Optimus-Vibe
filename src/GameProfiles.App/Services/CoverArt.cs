using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameProfiles.Logging;

namespace GameProfiles.App.Services;

/// <summary>Cover images (decoded small, frozen, cached by path) and the generated fallback tile colours.</summary>
public static class CoverArt
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Decode off the UI thread; null when the file is missing or unreadable.</summary>
    public static ImageSource? Load(string? path, int decodeWidth = 420)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Cache)
            if (Cache.TryGetValue(path, out var hit)) return hit;

        ImageSource? img = null;
        try
        {
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad; // read now, don't keep the file open
                bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Cover art {path}: {ex.Message}");
        }
        lock (Cache) Cache[path] = img;
        return img;
    }

    /// <summary>Accent → store colour diagonal gradient for games without art.</summary>
    public static Brush Fallback(string? store)
    {
        var accent = (Color)ColorConverter.ConvertFromString("#7C6CFF");
        var second = (store ?? "").ToLowerInvariant() switch
        {
            "steam" => Color.FromRgb(0x1B, 0x3A, 0x5C),
            "battlenet" => Color.FromRgb(0x0B, 0x4F, 0x8A),
            "ea" => Color.FromRgb(0x8A, 0x1E, 0x2E),
            "xbox" => Color.FromRgb(0x10, 0x6B, 0x2B),
            "epic" => Color.FromRgb(0x2A, 0x2D, 0x34),
            "gog" => Color.FromRgb(0x5B, 0x1E, 0x7A),
            _ => Color.FromRgb(0x25, 0x2A, 0x37),
        };
        var b = new LinearGradientBrush(Color.FromArgb(0xFF, (byte)(accent.R * 0.7), (byte)(accent.G * 0.7), (byte)(accent.B * 0.8)), second,
            new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    /// <summary>Segoe Fluent glyph shown on generated tiles.</summary>
    public static string Glyph(string? store) => (store ?? "").ToLowerInvariant() switch
    {
        "steam" => "",     // game controller
        "xbox" => "",      // xbox logo
        "battlenet" => "", // globe
        "ea" => "",
        "epic" => "",
        "gog" => "",
        _ => "",
    };

    /// <summary>"Cyberpunk 2077" → "C2"; "Borderlands 4" → "B4".</summary>
    public static string Initials(string name)
    {
        var words = name.Split([' ', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var s = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return s.Length == 0 ? "?" : s;
    }
}
