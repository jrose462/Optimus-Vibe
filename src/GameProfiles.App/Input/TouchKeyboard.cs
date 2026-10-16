using System.Diagnostics;
using System.Runtime.InteropServices;
using GameProfiles.Logging;

namespace GameProfiles.App.Input;

/// <summary>
/// Shows the Windows touch keyboard (TabTip) so text boxes are usable from a gamepad on the handheld.
/// Uses the ITipInvocation COM toggle when TabTip is running; otherwise starts TabTip.exe, which opens it.
/// </summary>
public static class TouchKeyboard
{
    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static readonly Guid UiHostNoLaunch = new("4ce576fa-83dc-4F88-951c-9d0782b4e376");

    public static void Show()
    {
        try
        {
            var wnd = FindWindow("IPTip_Main_Window", null);
            if (wnd != IntPtr.Zero && IsWindowVisible(wnd)) return; // already up

            var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
            if (Process.GetProcessesByName("TabTip").Length == 0)
            {
                if (File.Exists(tabTip)) Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true })?.Dispose();
                return;
            }
            var type = Type.GetTypeFromCLSID(UiHostNoLaunch);
            if (type is not null && Activator.CreateInstance(type) is ITipInvocation tip)
            {
                tip.Toggle(GetDesktopWindow());
                Marshal.ReleaseComObject(tip);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Touch keyboard: {ex.Message}");
        }
    }
}
