using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameProfiles.Library;
using GameProfiles.Logging;

namespace GameProfiles.App.Services;

/// <summary>Cover images and exe icons: decoded small, frozen, cached by path.</summary>
public static class CoverArt
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The game's exe icon (first exe found in the install dir, else the launch exe); null when there is none.</summary>
    public static ImageSource? LoadExeIcon(DetectedGame? game)
    {
        var exe = FindExe(game);
        if (exe is null) return null;
        lock (IconCache)
            if (IconCache.TryGetValue(exe, out var hit)) return hit;

        ImageSource? img = null;
        try
        {
            // Same icon Explorer shows (what Icon.ExtractAssociatedIcon returns), without pulling in System.Drawing.
            var info = new ShFileInfo();
            if (SHGetFileInfo(exe, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon) != 0 && info.hIcon != 0)
            {
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    img = src;
                }
                finally { DestroyIcon(info.hIcon); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Exe icon {exe}: {ex.Message}");
        }
        lock (IconCache) IconCache[exe] = img;
        return img;
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attrs, ref ShFileInfo info, uint size, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private static string? FindExe(DetectedGame? game)
    {
        if (game is null) return null;
        try
        {
            if (!string.IsNullOrEmpty(game.InstallDir) && Directory.Exists(game.InstallDir))
            {
                foreach (var name in game.Exes)
                {
                    var p = Path.Combine(game.InstallDir, name);
                    if (File.Exists(p)) return p;
                }
            }
            if (!string.IsNullOrEmpty(game.LaunchExe) && File.Exists(game.LaunchExe)) return game.LaunchExe;
        }
        catch (Exception) { /* bad path: no icon */ }
        return null;
    }
}
