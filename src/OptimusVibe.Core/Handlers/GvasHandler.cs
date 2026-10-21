using System.Text;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Handlers;

/// <summary>
/// Unreal Engine "GVAS" SaveGame files (binary property lists, e.g. Star Wars Jedi: Survivor's GameUserSettings.sav).
/// <para>
/// Keys are property paths: <c>ResolutionSizeX</c> (top-level), <c>Struct.Member</c> (struct member, e.g. <c>ResolutionSize.X</c> for an
/// IntPoint), <c>Array[2]</c> (scalar element) and <c>Array[2].Member</c>. Writable types: Int/UInt/Int64/Int16/Int8, Float, Double, Bool,
/// Byte (a number, or for an enum byte its value name; a number is refused for those), Enum, Str and Name properties. A fixed-size C array member appears as <c>Name</c> and <c>Name#1</c>, <c>Name#2</c>... Maps, sets, text and structs the reader does not
/// know stay as they are and are refused with an error when a preset targets them.
/// </para>
/// <para>
/// Unreal omits properties that equal their class defaults, so a key may be absent. Add a type hint after a colon to have the
/// handler insert it: <c>ShadowQuality:int</c> (int uint int64 uint64 int16 uint16 int8 float double bool byte str name enum).
/// Enums need their type: <c>Mode:enum(EWindowMode::Type): EWindowMode::Fullscreen</c>; an enum-as-byte <c>Key:byte(EFoo): EFoo::Bar</c>.
/// Reading ignores the hint; a hint that disagrees with an existing property's type is an error.
/// </para>
/// <para>
/// One key the save cannot take does not block the others: <see cref="WritePartial"/> writes the rest and names each refused key.
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{def.Id}: could not read {path}: {ex.Message}");
            return result;
        }
        foreach (var key in keyList)
        {
            try { result[key] = file.Get(GvasFile.SplitHint(key, out _)); }
            catch (GvasException ex) { Log.Warn($"{def.Id}: key '{key}': {ex.Message}"); }
        }
        return result;
    }

    /// <summary>Sets every key it can on <paramref name="file"/>; the ones it cannot are returned with the reason (a refused key changes nothing).</summary>
    private static (Dictionary<string, string> Accepted, Dictionary<string, string> Rejected) SetKeys(GvasFile file, IReadOnlyDictionary<string, string> values)
    {
        var accepted = new Dictionary<string, string>();
        var rejected = new Dictionary<string, string>();
        foreach (var (key, value) in values)
        {
            try { file.Set(key, value); accepted[key] = value; }
            catch (GvasException ex) { rejected[key] = ex.Message; }
        }
        return (accepted, rejected);
    }

    public override void Write(FileDef def, string path, IReadOnlyDictionary<string, string> values) => WriteCore(def, path, values, partial: false);

    public override IReadOnlyDictionary<string, string> WritePartial(FileDef def, string path, IReadOnlyDictionary<string, string> values) =>
        WriteCore(def, path, values, partial: true);

    public override IReadOnlyDictionary<string, string> Preflight(FileDef def, string path, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0 || !File.Exists(path)) return NoRejections;
        try { return SetKeys(GvasFile.Parse(File.ReadAllBytes(path)), values).Rejected; }
        catch (Exception ex) when (ex is GvasException or IOException or UnauthorizedAccessException) { return NoRejections; } // Read/Write report an unreadable file
    }

    private static Dictionary<string, string> WriteCore(FileDef def, string path, IReadOnlyDictionary<string, string> values, bool partial)
    {
        var none = new Dictionary<string, string>();
        if (values.Count == 0) return none;
        if (!File.Exists(path))
            throw new ConfigException($"{def.Id}: {path} does not exist; the gvas handler edits an existing save (launch the game once so it creates it).");
        byte[] original;
        GvasFile file;
        try
        {
            original = File.ReadAllBytes(path);
            file = GvasFile.Parse(original);
        }
        catch (GvasException ex) { throw new ConfigException($"{def.Id}: {ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ConfigException($"{def.Id}: could not read {path}: {ex.Message}"); }

        var (accepted, rejected) = SetKeys(file, values);
        if (rejected.Count > 0 && !partial)
            throw new ConfigException($"{def.Id}: " + string.Join("; ", rejected.Select(r => $"key '{r.Key}': {r.Value}")));
        if (accepted.Count == 0) return rejected;

        var bytes = file.ToBytes();
        if (bytes.AsSpan().SequenceEqual(original)) return rejected;

        // Safety net: what we are about to write must parse back to the values we set, and to itself.
        try
        {
            var check = GvasFile.Parse(bytes);
            foreach (var (key, value) in accepted)
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

        try { WriteAtomic(path, bytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ConfigException($"{def.Id}: could not write {path}: {ex.Message}"); }
        return rejected;
    }

    // The temp-file + File.Move replace keeps the write atomic but the new file gets the folder's default ACL, not the old file's.
    // Saves under the user profile inherit the same ACL, so nothing is lost in practice; a file with custom ACLs would lose them.
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"(could not read the save: {ex.Message})"; }
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
