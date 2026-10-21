using System.Text;
using OptimusVibe.Config;

namespace OptimusVibe.Handlers;

/// <summary>Reads and writes individual settings in one kind of config store.</summary>
public abstract class SettingsHandler
{
    public abstract string Name { get; }

    /// <summary>Whether this handler addresses a filesystem path (vs. e.g. a registry key).</summary>
    public virtual bool IsFile => true;

    /// <summary>Turn a YAML block into flat key → value. Default joins nested maps with '.'.</summary>
    public virtual Dictionary<string, string> Flatten(Dictionary<string, object?> block)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(string prefix, Dictionary<string, object?> map)
        {
            foreach (var (k, v) in map)
            {
                var key = prefix.Length == 0 ? k : prefix + "." + k;
                if (v is Dictionary<string, object?> nested) Walk(key, nested);
                else result[key] = Values.ToScalar(v);
            }
        }
        Walk("", block);
        return result;
    }

    /// <summary>Inverse of <see cref="Flatten"/> for writing captured values back to YAML.</summary>
    public virtual Dictionary<string, object?> Unflatten(IReadOnlyDictionary<string, string> flat) =>
        flat.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    /// <summary>Current values for the given keys; missing keys map to null.</summary>
    public abstract Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys);

    public abstract void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Every key of a store's text with its value, in the key syntax this handler's `files:` blocks use (so the lab can copy a key into a
    /// preset). Used by mapping runs to diff a file key by key. Null when the handler can't enumerate or the text isn't in its format.
    /// </summary>
    public virtual IReadOnlyList<(string Key, string Value)>? ReadAll(string text) => null;

    /// <summary>
    /// Human-readable listing of a binary store (key syntax, type, value) for <c>inspect</c>; null when the file is plain text and inspect
    /// prints it as is. <paramref name="full"/> lists every element of long arrays.
    /// </summary>
    public virtual string? Describe(string path, bool full) => null;

    public virtual bool Exists(string path) => File.Exists(path);

    /// <summary>Opaque copy of the whole store, or null if it does not exist.</summary>
    public virtual byte[]? Snapshot(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public virtual void RestoreSnapshot(string path, byte[]? data)
    {
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        if (data is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    public virtual void SetReadOnly(string path, bool readOnly)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        File.SetAttributes(path, readOnly ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly);
    }

    public static SettingsHandler For(string name) => name.ToLowerInvariant() switch
    {
        "ini" => new IniHandler(),
        "json" => new JsonHandler(),
        "xml" => new XmlHandler(),
        "registry" => new RegistryHandler(),
        "keyvalue" => new KeyValueTextHandler(),
        "gvas" => new GvasHandler(),
        "file" => new RawFileHandler(),
        _ => throw new ConfigException($"Unknown handler '{name}' (ini, json, xml, registry, keyvalue, gvas, file)"),
    };

    public static readonly string[] Names = ["ini", "json", "xml", "registry", "keyvalue", "gvas", "file"];
}

/// <summary>Line-oriented text file that keeps its encoding, BOM and newline style on save.</summary>
internal sealed class TextFile
{
    public List<string> Lines { get; }
    public Encoding Encoding { get; }
    public string NewLine { get; }
    public bool TrailingNewLine { get; }

    private TextFile(List<string> lines, Encoding encoding, string newLine, bool trailing)
    {
        Lines = lines; Encoding = encoding; NewLine = newLine; TrailingNewLine = trailing;
    }

    public static TextFile Load(string path)
    {
        if (!File.Exists(path)) return new TextFile(new List<string>(), new UTF8Encoding(false), "\r\n", true);
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, encoding.GetPreamble().Length, bytes.Length - encoding.GetPreamble().Length);
        if (text.Length == 0) return new TextFile(new List<string>(), encoding, "\r\n", true);
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        bool trailing = text.EndsWith('\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (trailing) lines.RemoveAt(lines.Count - 1);
        return new TextFile(lines, encoding, newLine, trailing);
    }

    public static string ReadAllText(string path) => Load(path) is var f ? string.Join(f.NewLine, f.Lines) : "";

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        var text = string.Join(NewLine, Lines) + (TrailingNewLine ? NewLine : "");
        using var stream = File.Create(path);
        var preamble = Encoding.GetPreamble();
        stream.Write(preamble);
        stream.Write(Encoding.GetBytes(text));
    }

    public static void SaveText(string path, string text, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        using var stream = File.Create(path);
        stream.Write(encoding.GetPreamble());
        stream.Write(encoding.GetBytes(text));
    }

    public static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return new UnicodeEncoding(false, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return new UnicodeEncoding(true, true);
        // UTF-16 LE without BOM (some Unreal ini files): every other byte zero
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0) return new UnicodeEncoding(false, false);
        return new UTF8Encoding(false);
    }
}
