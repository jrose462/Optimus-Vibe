using System.Reflection;

namespace OptimusVibe.Config;

/// <summary>
/// The generic starter config (repo folder seed\config: neutral example devices, the game template, the JSON schema and empty
/// library / request files), embedded in this assembly so a published exe can seed a new config repo or an empty folder
/// without any checkout. It never contains anyone's own devices, games or saves.
/// </summary>
public static class SeedConfig
{
    private const string Prefix = "seed/config/";

    /// <summary>Embedded files keyed by their path relative to the config folder ("devices.yaml", "games/_template.yaml"), '/' separators.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files()
    {
        var asm = typeof(SeedConfig).Assembly;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[name[Prefix.Length..]] = ms.ToArray();
        }
        if (!files.ContainsKey("devices.yaml"))
            throw new InvalidOperationException("The starter config is missing from this build (seed/config was not embedded).");
        return files;
    }

    /// <summary>
    /// Writes the starter config into <paramref name="configDir"/> (created if needed). Existing files are never overwritten.
    /// Returns the relative paths that were written.
    /// </summary>
    public static IReadOnlyList<string> WriteTo(string configDir)
    {
        var written = new List<string>();
        foreach (var (rel, content) in Files())
        {
            var target = Path.Combine(configDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
            written.Add(rel);
        }
        return written;
    }
}
