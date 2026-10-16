using System.Text;

namespace OptimusVibe.Tests;

/// <summary>A unique temp directory deleted on Dispose (clears read-only attributes first).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OptimusVibeTests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Dir(params string[] parts)
    {
        var d = File(parts);
        Directory.CreateDirectory(d);
        return d;
    }

    public string Write(string name, string text, Encoding? encoding = null)
    {
        var path = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Fixtures.Encode(text, encoding ?? new UTF8Encoding(false)));
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path)) return;
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class Fixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Fixture text with line endings normalized to <paramref name="newLine"/>.</summary>
    public static string Text(string name, string newLine = "\r\n")
    {
        var raw = File.ReadAllText(Path.Combine(Dir, name));
        return raw.Replace("\r\n", "\n").Replace("\n", newLine);
    }

    public static byte[] Encode(string text, Encoding encoding) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];

    /// <summary>Copy a fixture into the temp dir with the given newline and encoding.</summary>
    public static string CopyTo(TempDir dir, string name, string newLine = "\r\n", Encoding? encoding = null) =>
        dir.Write(name, Text(name, newLine), encoding);
}
