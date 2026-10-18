using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OptimusVibe.Tests;

/// <summary>
/// Credential tests write throwaway "OptimusVibe:test:&lt;guid&gt;" / "GameProfiles:test:&lt;guid&gt;" entries to the real
/// Windows Credential Manager and delete them in <c>finally</c>. A run that is killed or times out skips those, leaving
/// entries behind, so every test run also deletes all of them when the assembly loads and when the process exits.
/// Only these two test prefixes are ever touched.
/// </summary>
internal static class TestCredentialSweep
{
    private static readonly string[] Filters = ["OptimusVibe:test:*", "GameProfiles:test:*"];

    [ModuleInitializer]
    internal static void Init()
    {
        Sweep();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Sweep();
    }

    internal static int Sweep()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var deleted = 0;
        foreach (var filter in Filters)
        {
            if (!CredEnumerateW(filter, 0, out var count, out var list)) continue;
            try
            {
                var targets = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var p = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
                    if (cred.TargetName is { } t && (t.StartsWith("OptimusVibe:test:", StringComparison.Ordinal) || t.StartsWith("GameProfiles:test:", StringComparison.Ordinal)))
                        targets.Add(t);
                }
                foreach (var t in targets)
                    if (CredDeleteW(t, 1, 0)) deleted++;
            }
            finally
            {
                CredFree(list);
            }
        }
        return deleted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
