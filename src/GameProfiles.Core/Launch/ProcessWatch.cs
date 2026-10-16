using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameProfiles.Launch;

/// <summary>Finds processes by the folder their exe lives in (for games launched via a URI with no `processes:`).</summary>
internal static class ProcessWatch
{
    /// <summary>Full exe path of a process, or null when it cannot be read (access denied, exited, system process).</summary>
    public static string? ExePath(Process p)
    {
        try
        {
            // PROCESS_QUERY_LIMITED_INFORMATION also works for elevated processes (anti-cheat), unlike MainModule
            var h = OpenProcess(QueryLimitedInformation, false, p.Id);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString(0, size);
                }
                finally { CloseHandle(h); }
            }
        }
        catch (Exception) { /* fall through */ }

        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception) // Win32Exception (access denied), InvalidOperationException (exited), NotSupported
        {
            return null;
        }
    }

    public static bool IsUnder(string path, string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pids of processes currently running an exe under <paramref name="dir"/>.</summary>
    public static HashSet<int> PidsUnder(string dir)
    {
        var result = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4 || p.Id == Environment.ProcessId) continue;
                if (ExePath(p) is { } path && IsUnder(path, dir)) result.Add(p.Id);
            }
        }
        return result;
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
