using System.Text;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Handlers;

/// <summary>
/// Unreal Engine "GVAS" SaveGame files (binary property lists, e.g. Star Wars Jedi: Survivor's GameUserSettings.sav).
/// <para>
/// Keys are property paths: <c>ResolutionSizeX</c> (top-level), <c>Struct.Member</c> (struct member, e.g. <c>ResolutionSize.X</c> for an
/// IntPoint), <c>Array[2]</c> (scalar element) and <c>Array[2].Member</c>. Writable types: Int/UInt/Int64/Int16/Int8, Float, Double, Bool,
/// Byte (number, or the value name of an enum byte), Enum, Str and Name properties. Maps, sets, text and structs the reader does not
/// know stay as they are and are refused with an error when a preset targets them.
/// </para>
/// <para>
/// Unreal omits properties that equal their class defaults, so a key may be absent. Add a type hint after a colon to have the
/// handler insert it: <c>ShadowQuality:int</c> (int uint int64 uint64 int16 uint16 int8 float double bool byte str name enum).
/// Reading ignores the hint; a hint that disagrees with an existing property's type is an error.
/// </para>
/// A write parses the file, changes only the named properties, re-parses its own output as a safety check and replaces the file
/// atomically; every other byte is preserved. A file that already matches is not rewritten.
/// </summary>
public sealed class GvasHandler : SettingsHandler
{
    public override string Name => "gvas";

    public override Dictionary<string, string?> Read(FileDef def, string path, IEnumerable<string> keys)
    {
        var keyList = keys.ToList();
        var result = keyList.ToDictionary(k => k, _ => (string?)null);
        if (!File.Exists(path) || keyList.Count == 0) return result;
        GvasFile file;
        try { file = GvasFile.Parse(File.ReadAllBytes(path)); }
        catch (GvasException ex)
        {
            Log.Warn($"{def.Id}: {path} is not readable as a GVAS save: {ex.Message}");
            return result;
        }
        foreach (var key in keyList)
        {
            try { result[key] = file.Get(GvasFile.SplitHint(key, out _)); }
            catch (GvasException ex) { Log.Warn($"{def.Id}: key '{key}': {ex.Message}"); }
        }
        return result;
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;
        if (!File.Exists(path))
            throw new ConfigException($"{def.Id}: {path} does not exist; the gvas handler edits an existing save (launch the game once so it creates it).");
        var original = File.ReadAllBytes(path);
        GvasFile file;
        try
        {
            file = GvasFile.Parse(original);
            foreach (var (key, value) in values) file.Set(key, value);
        }
        catch (GvasException ex)
        {
            throw new ConfigException($"{def.Id}: {ex.Message}");
        }

        var bytes = file.ToBytes();
        if (bytes.AsSpan().SequenceEqual(original)) return;

        // Safety net: what we are about to write must parse back to the values we set, and to itself.
        try
        {
            var check = GvasFile.Parse(bytes);
            foreach (var (key, value) in values)
            {
                var back = check.Get(GvasFile.SplitHint(key, out _));
                if (back is null || !Values.Equivalent(back, value) && !string.Equals(back, value, StringComparison.Ordinal))
                    throw new GvasException($"'{key}' reads back as '{back}', not '{value}'.");
            }
            if (!check.ToBytes().AsSpan().SequenceEqual(bytes)) throw new GvasException("the edited save does not round-trip.");
        }
        catch (GvasException ex)
        {
            throw new ConfigException($"{def.Id}: refusing to write {path}: {ex.Message}");
        }

        WriteAtomic(path, bytes);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        var temp = Path.Combine(dir, Path.GetFileName(path) + ".ov-tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Every readable property as <c>Path</c> -> value, for mapping (keys can be pasted into a preset).</summary>
    public static IReadOnlyList<(string Key, string Value)> Entries(byte[] data) =>
        GvasFile.Parse(data).Enumerate().Where(e => e.Writable).Select(e => (e.Path, e.Value)).ToList();

    public override string? Describe(string path, bool full)
    {
        GvasFile file;
        try { file = GvasFile.Parse(File.ReadAllBytes(path)); }
        catch (GvasException ex) { return $"(not a readable GVAS save: {ex.Message})"; }
        var sb = new StringBuilder();
        sb.AppendLine($"GVAS save, class {file.SaveClass}, engine {file.EngineVersion}. Keys below are what a `files:` block for this file uses (read-only rows cannot be written).");
        const int MaxShown = 8;
        string? collapsed = null;
        foreach (var e in file.Enumerate())
        {
            if (collapsed is not null && e.Path.StartsWith(collapsed, StringComparison.Ordinal)) continue;
            collapsed = null;
            sb.AppendLine($"{(e.Writable ? "  " : "ro")} {e.Path} [{e.Type}] = {e.Value}");
            if (!full && e.Type.StartsWith("ArrayProperty<", StringComparison.Ordinal) && int.TryParse(e.Value.Trim('[', ']'), out var n) && n > MaxShown)
            {
                collapsed = e.Path + "[";
                sb.AppendLine($"   ({n} elements not listed; use --full)");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
