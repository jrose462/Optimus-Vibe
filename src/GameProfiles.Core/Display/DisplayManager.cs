using System.Runtime.InteropServices;
using GameProfiles.Config;
using GameProfiles.Logging;

namespace GameProfiles.Display;

public sealed record DisplayModeState(string DeviceName, int Width, int Height, int Refresh)
{
    public override string ToString() => $"{Width}x{Height}@{Refresh}Hz ({DeviceName})";
}

public sealed class DisplayException(string message) : Exception(message);

/// <summary>
/// Reads and changes the resolution/refresh of the primary display via user32
/// (EnumDisplayDevicesW / EnumDisplaySettingsW / ChangeDisplaySettingsExW).
/// Changes are dynamic (not written to the registry), so a reboot also reverts them.
/// </summary>
public static class DisplayManager
{
    /// <summary>Current mode of the primary attached display, or null if it cannot be determined.</summary>
    public static DisplayModeState? GetCurrent()
    {
        var device = GetPrimaryDeviceName();
        if (device == null) return null;
        var dm = NewDevMode();
        if (!Native.EnumDisplaySettingsW(device, Native.ENUM_CURRENT_SETTINGS, ref dm)) return null;
        return new DisplayModeState(device, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
    }

    /// <summary>Distinct width/height/refresh combinations of the primary display (32bpp modes if any exist, otherwise all).</summary>
    public static IReadOnlyList<DisplayModeState> GetSupportedModes()
    {
        var device = GetPrimaryDeviceName();
        if (device == null) return Array.Empty<DisplayModeState>();
        return EnumModes(device);
    }

    /// <summary>
    /// Applies <paramref name="target"/> to the primary display (null fields keep the current value).
    /// Returns the previous mode when a change was made, or null when the display already matches.
    /// </summary>
    public static DisplayModeState? Apply(DisplayModeDef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var current = GetCurrent() ?? throw new DisplayException("Could not read the current mode of the primary display.");

        var width = target.Width ?? current.Width;
        var height = target.Height ?? current.Height;
        var refresh = target.Refresh ?? current.Refresh;

        if (width == current.Width && height == current.Height && refresh == current.Refresh)
            return null;

        var modes = EnumModes(current.DeviceName);
        var atResolution = modes.Where(m => m.Width == width && m.Height == height).Select(m => m.Refresh).Distinct().ToList();
        if (modes.Count > 0 && atResolution.Count == 0)
            throw new DisplayException($"{width}x{height} is not a supported resolution on {current.DeviceName}.");

        if (atResolution.Count > 0 && !atResolution.Contains(refresh))
        {
            var lower = atResolution.Where(r => r <= refresh).ToList();
            var chosen = lower.Count > 0
                ? lower.Max()
                : atResolution.OrderBy(r => Math.Abs(r - refresh)).First();
            Log.Warn($"Display: {refresh}Hz is not supported at {width}x{height}; using {chosen}Hz instead " +
                     $"(supported: {string.Join(", ", atResolution.OrderBy(r => r))}).");
            refresh = chosen;
        }

        if (width == current.Width && height == current.Height && refresh == current.Refresh)
            return null;

        SetMode(current.DeviceName, width, height, refresh);
        Log.Info($"Display: {current.Width}x{current.Height}@{current.Refresh}Hz -> {width}x{height}@{refresh}Hz ({current.DeviceName})");
        return current;
    }

    /// <summary>Applies exactly <paramref name="previous"/> to its device.</summary>
    public static void Restore(DisplayModeState previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        SetMode(previous.DeviceName, previous.Width, previous.Height, previous.Refresh);
        Log.Info($"Display: restored {previous}");
    }

    // ---------------------------------------------------------------------------------------------

    private static void SetMode(string device, int width, int height, int refresh)
    {
        var dm = NewDevMode();
        if (!Native.EnumDisplaySettingsW(device, Native.ENUM_CURRENT_SETTINGS, ref dm))
            throw new DisplayException($"Could not read the current settings of {device} (is it still attached?).");

        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmDisplayFrequency = (uint)refresh;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;

        var test = Native.ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
        if (test != Native.DISP_CHANGE_SUCCESSFUL)
            throw new DisplayException($"{width}x{height}@{refresh}Hz rejected by {device} (test): {DispChangeName(test)} ({test}).");

        // Flags 0 = dynamic change of the current mode; not persisted to the registry.
        var result = Native.ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        if (result != Native.DISP_CHANGE_SUCCESSFUL)
            throw new DisplayException($"Changing {device} to {width}x{height}@{refresh}Hz failed: {DispChangeName(result)} ({result}).");
    }

    private static List<DisplayModeState> EnumModes(string device)
    {
        var all = new List<(uint Bpp, DisplayModeState Mode)>();
        var dm = NewDevMode();
        for (var i = 0; Native.EnumDisplaySettingsW(device, i, ref dm); i++)
        {
            all.Add((dm.dmBitsPerPel, new DisplayModeState(device, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency)));
            dm = NewDevMode();
        }

        var preferred = all.Any(a => a.Bpp == 32) ? all.Where(a => a.Bpp == 32) : all;
        return preferred
            .Select(a => a.Mode)
            .Distinct()
            .OrderByDescending(m => m.Width)
            .ThenByDescending(m => m.Height)
            .ThenByDescending(m => m.Refresh)
            .ToList();
    }

    private static string? GetPrimaryDeviceName()
    {
        var dd = NewDisplayDevice();
        for (uint i = 0; Native.EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0 &&
                (dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                return dd.DeviceName;
            dd = NewDisplayDevice();
        }
        return null;
    }

    private static Native.DEVMODEW NewDevMode() => new()
    {
        dmDeviceName = "",
        dmFormName = "",
        dmSize = (ushort)Marshal.SizeOf<Native.DEVMODEW>(),
    };

    private static Native.DISPLAY_DEVICEW NewDisplayDevice() => new()
    {
        cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICEW>(),
        DeviceName = "",
        DeviceString = "",
        DeviceID = "",
        DeviceKey = "",
    };

    private static string DispChangeName(int code) => code switch
    {
        0 => "DISP_CHANGE_SUCCESSFUL",
        1 => "DISP_CHANGE_RESTART",
        -1 => "DISP_CHANGE_FAILED",
        -2 => "DISP_CHANGE_BADMODE",
        -3 => "DISP_CHANGE_NOTUPDATED",
        -4 => "DISP_CHANGE_BADFLAGS",
        -5 => "DISP_CHANGE_BADPARAM",
        -6 => "DISP_CHANGE_BADDUALVIEW",
        _ => "DISP_CHANGE_UNKNOWN",
    };

    private static class Native
    {
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_TEST = 0x00000002;
        public const int DISP_CHANGE_SUCCESSFUL = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICEW
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        /// <summary>DEVMODEW, display variant of the unions (220 bytes).</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            // union { printer fields | { POINTL dmPosition; DWORD dmDisplayOrientation; DWORD dmDisplayFixedOutput; } }
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags; // union with dmNup
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int ChangeDisplaySettingsExW(string lpszDeviceName, ref DEVMODEW lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
    }
}
