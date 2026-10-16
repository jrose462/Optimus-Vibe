using Microsoft.Win32;

namespace OptimusVibe.Library.Scanners;

/// <summary>Read-only registry access for the scanners (faked in tests). Keys are written "HKLM\SOFTWARE\..." / "HKCU\...".</summary>
internal interface IRegistryReader
{
    /// <summary>Sub key names of a key; empty when the key does not exist.</summary>
    IReadOnlyList<string> SubKeys(string key);

    /// <summary>A value as a string (numbers converted), or null when the key or value is missing.</summary>
    string? Value(string key, string name);
}

/// <summary>The real registry, always the 64-bit view (paths spell out WOW6432Node explicitly).</summary>
internal sealed class WindowsRegistryReader : IRegistryReader
{
    public static readonly WindowsRegistryReader Instance = new();

    public IReadOnlyList<string> SubKeys(string key)
    {
        try
        {
            using var k = Open(key);
            return k?.GetSubKeyNames() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public string? Value(string key, string name)
    {
        try
        {
            using var k = Open(key);
            return k?.GetValue(name) switch
            {
                null => null,
                string s => s,
                string[] arr => string.Join(";", arr),
                byte[] => null,
                var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return null;
        }
    }

    private static RegistryKey? Open(string key)
    {
        var slash = key.IndexOf('\\');
        var hiveName = slash < 0 ? key : key[..slash];
        var sub = slash < 0 ? "" : key[(slash + 1)..];
        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            _ => throw new ArgumentException($"Unknown hive in '{key}'"),
        };
        var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (sub.Length == 0) return root;
        try
        {
            return root.OpenSubKey(sub);
        }
        finally
        {
            root.Dispose();
        }
    }
}
