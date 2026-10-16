using System.Runtime.InteropServices;

namespace OptimusVibe.Ui;

/// <summary>Tiny user32/kernel32 UI helpers (no WinForms dependency).</summary>
public static class NativeUi
{
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_SYSTEMMODAL = 0x00001000;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;
    private const int IDYES = 6;
    private const int SW_HIDE = 0;

    /// <summary>
    /// Yes/No question box. Flags:
    /// <list type="bullet">
    /// <item>MB_TOPMOST: the box gets WS_EX_TOPMOST so it sits above the full-screen Xbox shell / Steam Big Picture,
    /// which are themselves topmost-style windows.</item>
    /// <item>MB_SETFOREGROUND: asks Windows to make it the foreground window so a controller/keyboard can answer it
    /// (Windows may refuse and flash the taskbar instead if the wrapper is not allowed to take the foreground).</item>
    /// <item>MB_SYSTEMMODAL: with no owner window this is the documented way to get a topmost message box; it adds no
    /// extra blocking beyond our own thread.</item>
    /// </list>
    /// MB_SERVICE_NOTIFICATION / MB_DEFAULT_DESKTOP_ONLY are deliberately not used: they are meant for services, and the
    /// first ignores the owner/foreground rules in ways that can put the box on the wrong desktop.
    /// Returns false on "No" or if the box could not be shown.
    /// </summary>
    public static bool AskYesNo(string title, string message)
    {
        try
        {
            var result = MessageBoxW(IntPtr.Zero, message ?? "", title ?? "",
                MB_YESNO | MB_ICONQUESTION | MB_TOPMOST | MB_SETFOREGROUND | MB_SYSTEMMODAL);
            return result == IDYES;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hides the console window when this process is its only user (i.e. the console was created just for us,
    /// e.g. when Steam starts the wrapper). Does nothing when started from an existing terminal.
    /// Note: when Windows Terminal is the default terminal host, GetConsoleWindow returns a pseudo window and
    /// hiding it has no visible effect; such launches still show a terminal tab.
    /// </summary>
    public static void HideConsoleIfOwned()
    {
        try
        {
            var pids = new uint[4];
            var count = GetConsoleProcessList(pids, (uint)pids.Length);
            if (count != 1) return;
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
        }
        catch
        {
            // best effort
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] lpdwProcessList, uint dwProcessCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
