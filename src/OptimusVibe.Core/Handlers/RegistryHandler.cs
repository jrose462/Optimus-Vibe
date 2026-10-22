using System.Globalization;
using System.Text.Json;
using OptimusVibe.Config;
using Microsoft.Win32;

namespace OptimusVibe.Handlers;

/// <summary>
/// Registry values under one key, e.g. <c>HKCU\Software\Blizzard Entertainment\...</c>. Keys are value names
/// (use "(default)" for the default value). Existing value kinds are preserved; new values become DWORD when the
/// value is an integer, otherwise REG_SZ. Binary values are hex strings.
/// </summary>
public sealed class RegistryHandler : SettingsHandler
{
    public override string Name => "registry";
    public override bool IsFile => false;

    public override Dictionary<string, string> Flatten(Dictionary<string, object?> block) =>
        block.ToDictionary(kv => kv.Key, kv => Values.ToScalar(kv.Value), StringComparer.Ordinal);

    public override bool Exists(string path)
    {
        using var key = Open(path, writable: false);
        return key is not null;
    }

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys)
    {
        using var key = Open(path, writable: false);
        return keys.ToDictionary(k => k, k =>
        {
            if (key is null) return null;
            var name = ValueName(k);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return raw is null ? null : Format(raw, key.GetValueKind(name));
        });
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        using var key = Open(path, writable: true, create: true)!;
        foreach (var (k, value) in values)
        {
            var name = ValueName(k);
            var kind = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
                ? key.GetValueKind(name)
                : KindForNewValue(value);
            key.SetValue(name, ParseValue(value, kind), kind);
        }
    }

    public override byte[]? Snapshot(string path)
    {
        using var key = Open(path, writable: false);
        if (key is null) return null;
        var dump = key.GetValueNames().ToDictionary(n => n, n => new RegValue(
            key.GetValueKind(n).ToString(),
            Format(key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(n))));
        return JsonSerializer.SerializeToUtf8Bytes(dump);
    }

    public override void RestoreSnapshot(string path, byte[]? data)
    {
        if (data is null)
        {
            using var parentCheck = Open(path, writable: false);
            if (parentCheck is null) return;
            var (hive, sub) = SplitPath(path);
            if (parentCheck.SubKeyCount == 0) hive.DeleteSubKey(sub, throwOnMissingSubKey: false);
            return;
        }
        var dump = JsonSerializer.Deserialize<Dictionary<string, RegValue>>(data)!;
        using var key = Open(path, writable: true, create: true)!;
        foreach (var name in key.GetValueNames().Where(n => !dump.ContainsKey(n)))
            key.DeleteValue(name, throwOnMissingValue: false);
        foreach (var (name, v) in dump)
        {
            var kind = Enum.Parse<RegistryValueKind>(v.Kind);
            key.SetValue(name, ParseValue(v.Data, kind), kind);
        }
    }

    public override void SetReadOnly(string path, bool readOnly) { }

    private sealed record RegValue(string Kind, string Data);

    private static string ValueName(string key) => key == "(default)" ? "" : key;

    /// <summary>Integers that fit 32 bits → DWORD, larger → QWORD, anything else → REG_SZ.</summary>
    private static RegistryValueKind KindForNewValue(string value)
    {
        if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return RegistryValueKind.DWord;
        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return RegistryValueKind.QWord;
        return RegistryValueKind.String;
    }

    /// <remarks>
    /// The declared kind and the data type can disagree (Unity PlayerPrefs and similar write a REG_DWORD whose
    /// data comes back as bytes), so the format follows the runtime type of the data, not the kind.
    /// </remarks>
    internal static string Format(object raw, RegistryValueKind kind) => raw switch
    {
        int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        string[] m => string.Join("\n", m),
        _ => raw.ToString() ?? "",
    };

    private static object ParseValue(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)ParseUnsigned(value)),
        RegistryValueKind.QWord => unchecked((long)ParseUnsigned(value)),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromHexString(value.Replace(" ", "").Replace(",", "")),
        RegistryValueKind.MultiString => value.Split('\n'),
        _ => value,
    };

    private static ulong ParseUnsigned(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (bool.TryParse(value, out var b)) return b ? 1UL : 0UL;
        if (value.StartsWith('-')) return unchecked((ulong)long.Parse(value, CultureInfo.InvariantCulture));
        return ulong.Parse(value, CultureInfo.InvariantCulture);
    }

    internal static (RegistryKey Hive, string SubKey) SplitPath(string path)
    {
        path = path.Replace('/', '\\').Trim('\\');
        int slash = path.IndexOf('\\');
        var hiveName = slash < 0 ? path : path[..slash];
        var sub = slash < 0 ? "" : path[(slash + 1)..];
        RegistryKey hive = hiveName.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => throw new ConfigException($"Registry path must start with HKCU or HKLM: {path}"),
        };
        return (hive, sub);
    }

    private static RegistryKey? Open(string path, bool writable, bool create = false)
    {
        var (hive, sub) = SplitPath(path);
        return create ? hive.CreateSubKey(sub, writable) : hive.OpenSubKey(sub, writable);
    }
}
