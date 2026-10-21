using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OptimusVibe.Handlers;
using OptimusVibe.Logging;

namespace OptimusVibe.GameLab;

/// <summary>Bounds of a mapping run, so a huge folder can never stall it or fill memory.</summary>
public sealed class ProbeLimits
{
    /// <summary>Files fingerprinted per snapshot; the rest are named in the notes.</summary>
    public int MaxFiles { get; init; } = 2000;

    /// <summary>Bytes hashed per snapshot (all files together); beyond it a file is compared by size and time only.</summary>
    public long MaxHashBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Text files up to this size keep their text, so a changed file can be diffed key by key.</summary>
    public int MaxTextBytes { get; init; } = 512 * 1024;

    /// <summary>All kept text together (bytes).</summary>
    public long MaxTextTotal { get; init; } = 24L * 1024 * 1024;

    /// <summary>Registry keys dumped per snapshot (a key and its sub keys, two levels down).</summary>
    public int MaxRegistryKeys { get; init; } = 300;

    /// <summary>Key changes listed per changed file (the rest are counted).</summary>
    public int MaxKeyChanges { get; init; } = 150;

    /// <summary>The same for a file that did not exist before (every key of a new settings file matters, so more room).</summary>
    public int MaxNewFileKeys { get; init; } = 400;

    /// <summary>Line changes listed for a text file whose format isn't recognised.</summary>
    public int MaxLineChanges { get; init; } = 60;

    public int MaxValueChars { get; init; } = 200;
}

/// <summary>One file as the snapshot saw it.</summary>
/// <param name="Token">The tokenized path ({LocalAppData}\Foo\a.ini); this is the identity of the file.</param>
/// <param name="Sha256">Null when the hashing budget was used up (the file is then compared by size and time).</param>
/// <param name="Text">The decoded text of a small text file; null for binary or big files.</param>
public sealed record ProbeFile(string Token, long Size, DateTime ModifiedUtc, string? Sha256, string? Text)
{
    public bool IsText => Text is not null;
}

/// <summary>Everything a mapping run saw before or after a launch.</summary>
public sealed class ProbeSnapshot
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, ProbeFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full key (HKCU\Software\...) → (value name, value text).</summary>
    public Dictionary<string, IReadOnlyList<(string Name, string Value)>> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Notes { get; } = [];

    /// <summary>The folders (tokenized) that were walked, for the evidence header.</summary>
    public List<string> WatchedDirs { get; } = [];
}

public enum ProbeChangeKind { Added, Removed, Changed }

/// <summary>One setting of a text file that is different now. <c>Before</c> null = new key, <c>After</c> null = key gone.</summary>
public sealed record KeyChange(string Key, string? Before, string? After);

/// <param name="Format">How the keys were read: ini | json | xml | keyvalue | lines | "" (binary or unreadable).</param>
/// <param name="KeysOmitted">Key changes not listed because of the limit.</param>
public sealed record FileChange(string Path, ProbeChangeKind Kind, long? SizeBefore, long? SizeAfter, bool IsText, string? HashBefore, string? HashAfter,
    string Format, IReadOnlyList<KeyChange> Keys, int KeysOmitted)
{
    public bool IsBinary => !IsText;
}

public sealed record RegistryChange(string Key, string Name, string? Before, string? After);

/// <summary>What one launch changed.</summary>
public sealed record ProbeDiff(IReadOnlyList<FileChange> Files, IReadOnlyList<RegistryChange> Registry, IReadOnlyList<string> Notes, IReadOnlyList<string>? Watched = null)
{
    public bool IsEmpty => Files.Count == 0 && Registry.Count == 0;
}

/// <summary>
/// The text of a mapping run's evidence in its two parts, plus short lines for the screen.
/// </summary>
/// <param name="Header">What was watched and the bounds.</param>
/// <param name="PassA">"Rewritten every session": files and keys the game changed by itself while the player changed nothing.</param>
/// <param name="PassB">"Settings changed": changes beyond pass A, with exact key names and values.</param>
/// <param name="StateLines">Short lines for pass A (screen).</param>
/// <param name="SettingsLines">Short lines for pass B (screen).</param>
/// <param name="OpaqueFiles">Tokenized paths of binary files that changed only in pass B: settings the lab cannot read key by key.</param>
/// <param name="ReadableChanges">Pass B changes that can be read key by key: text-file keys (or new text files) and registry values.</param>
/// <param name="PassBRan">Pass B was run (without it nothing can be said about where the settings live).</param>
public sealed record ProbeEvidence(string Header, string PassA, string PassB, IReadOnlyList<string> StateLines, IReadOnlyList<string> SettingsLines,
    IReadOnlyList<string> OpaqueFiles, IReadOnlyList<string> Notes, int ReadableChanges = 0, bool PassBRan = false)
{
    /// <summary>The whole body of the "### Mapping run" section.</summary>
    public string Render() => $"{Header}\n\n{PassA}\n\n{PassB}".TrimEnd();

    public bool HasSettings => SettingsLines.Count > 0;

    /// <summary>
    /// Pass B changed only binary files (no text key, no registry value): the settings are in a store Optimus-Vibe cannot write key by key,
    /// so flagging the game "set in-game" is worth suggesting. Never applied automatically.
    /// </summary>
    public bool SuggestsSetInGame => PassBRan && OpaqueFiles.Count > 0 && ReadableChanges == 0;

    /// <summary>The one-line reason behind <see cref="SuggestsSetInGame"/> ("the game keeps its settings only in a binary file (system.save) Optimus-Vibe can't edit").</summary>
    public string? SuggestionReason => SuggestsSetInGame ? Library.GameFlags.MappingSuggestionReason(OpaqueFiles) : null;
}

/// <summary>
/// A mapping run: fingerprints every file under the game's candidate folders (the same ones the evidence collector finds by name or
/// publisher), the install's config files, the files the config already maps, and the registry keys under HKCU\Software that belong to the
/// game, before and after a real launch. <see cref="Diff"/> says what the launch changed (key by key for text files, hash only for binary
/// ones, old -> new for registry values); <see cref="ToEvidence"/> turns the two passes into the lab's "### Mapping run" evidence.
/// Read-only: nothing is restored, edited or deleted. Never sends binary contents.
/// </summary>
public sealed class ProbeSession
{
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "log", "crashes", "crashreportclient", "crashreports", "crash", "crashpad", "webcache", "cache", "shadercache", "gpucache", "code cache",
        "dxcache", "d3dscache", "glcache", "blob_storage", "screenshots", "videos", "movies", "telemetry", "temp", "tmp",
    };

    private static readonly Regex SecretKey = new(@"(?i)(token|passw(or)?d|passwd|secret|api[_-]?key|cookie|session[_-]?id|authorization|credential|private[_-]?key)", RegexOptions.Compiled);

    private readonly LabTarget _target;
    private readonly EvidenceOptions _options;
    private readonly ProbeLimits _limits;

    public ProbeSession(LabTarget target, EvidenceOptions? options = null, ProbeLimits? limits = null)
    {
        _target = target;
        _options = options ?? new EvidenceOptions();
        _limits = limits ?? new ProbeLimits();
    }

    public LabTarget Target => _target;
    public ProbeLimits Limits => _limits;

    // ------------------------------------------------------------ snapshot

    /// <summary>
    /// Fingerprints the game's candidate folders, mapped files and registry keys right now. The scope is looked up again on every call,
    /// so a folder the first launch creates shows up in the second snapshot. Never throws.
    /// </summary>
    public ProbeSnapshot Snapshot()
    {
        var scope = new EvidenceCollector(_options).DiscoverProbeScope(_target);
        return Snapshot(scope);
    }

    /// <summary>Fingerprints an explicit scope (tests, or a scope computed once).</summary>
    public ProbeSnapshot Snapshot(EvidenceCollector.ProbeScope scope)
    {
        var snap = new ProbeSnapshot();
        snap.Notes.AddRange(scope.Notes);
        var folders = scope.Folders;
        var budget = new Budget(_limits);
        try
        {
            foreach (var dir in scope.Dirs)
            {
                snap.WatchedDirs.Add(folders.Tokenize(dir));
                if (!Directory.Exists(dir)) continue;
                Walk(dir, 0, folders, snap, budget);
            }
            foreach (var file in scope.Files) AddFile(file, folders, snap, budget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snap.Notes.Add($"Reading the folders stopped early: {Clean(ex.Message, folders)}");
        }
        if (budget.FilesCut > 0) snap.Notes.Add($"{budget.FilesCut} more file(s) were not fingerprinted (limit {_limits.MaxFiles} files).");
        if (budget.Unhashed > 0) snap.Notes.Add($"{budget.Unhashed} file(s) were compared by size and time only (limit {_limits.MaxHashBytes / (1024 * 1024)} MB hashed).");

        try
        {
            var regBudget = _limits.MaxRegistryKeys;
            foreach (var key in scope.RegistryKeys) DumpRegistry(key, 2, snap, ref regBudget);
            if (regBudget <= 0) snap.Notes.Add($"Registry dump stopped at {_limits.MaxRegistryKeys} keys.");
        }
        catch (Exception ex)
        {
            snap.Notes.Add($"Registry read failed: {Clean(ex.Message, folders)}");
        }
        return snap;
    }

    private sealed class Budget(ProbeLimits limits)
    {
        public int Files;
        public int FilesCut;
        public long Hashed;
        public long Text;
        public int Unhashed;
        public bool CanAddFile() { if (Files >= limits.MaxFiles) { FilesCut++; return false; } return true; }
    }

    private void Walk(string dir, int depth, LabFolders folders, ProbeSnapshot snap, Budget budget)
    {
        foreach (var f in SafeFiles(dir)) AddFile(f, folders, snap, budget);
        if (depth >= 8) return;
        foreach (var sub in SafeDirs(dir))
        {
            if (SkipDirNames.Contains(Path.GetFileName(sub))) continue;
            Walk(sub, depth + 1, folders, snap, budget);
        }
    }

    private void AddFile(string path, LabFolders folders, ProbeSnapshot snap, Budget budget)
    {
        var token = folders.Tokenize(path);
        if (snap.Files.ContainsKey(token)) return;
        if (EvidencePolicy.CheckPath(token, folders, out _) is not null) return; // the same rules as every other evidence
        if (!budget.CanAddFile()) return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return;
            string? sha = null, text = null;
            if (info.Length <= _limits.MaxTextBytes && budget.Hashed + info.Length <= _limits.MaxHashBytes)
            {
                var bytes = ReadAll(path, info.Length);
                sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                budget.Hashed += bytes.Length;
                if (budget.Text + bytes.Length <= _limits.MaxTextTotal && EvidenceCollector.TryDecodeText(bytes, out var decoded))
                {
                    text = decoded;
                    budget.Text += bytes.Length;
                }
            }
            else if (budget.Hashed + info.Length <= _limits.MaxHashBytes)
            {
                sha = HashStream(path);
                budget.Hashed += info.Length;
            }
            else
            {
                budget.Unhashed++;
            }
            budget.Files++;
            snap.Files[token] = new ProbeFile(token, info.Length, info.LastWriteTimeUtc, sha, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snap.Notes.Add($"Could not read {Clean(token, folders)}: {Clean(ex.Message, folders)}");
        }
    }

    private static byte[] ReadAll(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(length, int.MaxValue)];
        var read = 0;
        while (read < bytes.Length) { var n = fs.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
        if (read < bytes.Length) Array.Resize(ref bytes, read);
        return bytes;
    }

    private static string HashStream(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private void DumpRegistry(string key, int depth, ProbeSnapshot snap, ref int budget)
    {
        if (budget <= 0) return;
        var values = _options.Registry.Values(key);
        if (values is not null)
        {
            snap.Registry[key] = values;
            budget--;
        }
        if (depth <= 0) return;
        foreach (var sub in _options.Registry.SubKeyNames(key))
        {
            if (budget <= 0) return;
            DumpRegistry(key + "\\" + sub, depth - 1, snap, ref budget);
        }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).Where(d =>
            {
                try { return (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ------------------------------------------------------------ diff

    /// <summary>What changed between two snapshots: added, removed and changed files (key level for text), and registry values.</summary>
    public static ProbeDiff Diff(ProbeSnapshot before, ProbeSnapshot after, ProbeLimits? limits = null)
    {
        limits ??= new ProbeLimits();
        var files = new List<FileChange>();
        foreach (var (token, now) in after.Files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.Files.TryGetValue(token, out var was))
            {
                var (format, keys, omitted) = DiffText(null, now.Text, token, limits, limits.MaxNewFileKeys);
                files.Add(new FileChange(token, ProbeChangeKind.Added, null, now.Size, now.IsText, null, now.Sha256, format, keys, omitted));
                continue;
            }
            if (!Differs(was, now)) continue;
            var (fmt, changes, more) = was.IsText && now.IsText ? DiffText(was.Text, now.Text, token, limits, limits.MaxKeyChanges) : ("", [], 0);
            files.Add(new FileChange(token, ProbeChangeKind.Changed, was.Size, now.Size, now.IsText, was.Sha256, now.Sha256, fmt, changes, more));
        }
        foreach (var (token, was) in before.Files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (!after.Files.ContainsKey(token))
                files.Add(new FileChange(token, ProbeChangeKind.Removed, was.Size, null, was.IsText, was.Sha256, null, "", [], 0));

        var registry = new List<RegistryChange>();
        foreach (var key in before.Registry.Keys.Concat(after.Registry.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var a = before.Registry.TryGetValue(key, out var av) ? av.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase) : [];
            var b = after.Registry.TryGetValue(key, out var bv) ? bv.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase) : [];
            foreach (var name in a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                a.TryGetValue(name, out var x);
                b.TryGetValue(name, out var y);
                if (x != y) registry.Add(new RegistryChange(key, name, x, y));
            }
        }

        var notes = before.Notes.Concat(after.Notes).Distinct().ToList();
        return new ProbeDiff(files, registry, notes, after.WatchedDirs.Concat(before.WatchedDirs).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool Differs(ProbeFile a, ProbeFile b) =>
        a.Sha256 is not null && b.Sha256 is not null ? a.Sha256 != b.Sha256 : a.Size != b.Size || a.ModifiedUtc != b.ModifiedUtc;

    private static (string Format, IReadOnlyList<KeyChange> Keys, int Omitted) DiffText(string? before, string? after, string token, ProbeLimits limits, int max)
    {
        if (after is null && before is null) return ("", [], 0);
        var ext = Path.GetExtension(token).ToLowerInvariant();
        var handler = ext switch
        {
            ".ini" or ".cfg" or ".conf" => (SettingsHandler)new IniHandler(),
            ".json" => new JsonHandler(),
            ".xml" or ".lsx" or ".config" => new XmlHandler(),
            _ => null,
        };

        string format = "";
        IReadOnlyList<(string Key, string Value)>? a = null, b = null;
        if (handler is not null)
        {
            a = before is null ? [] : handler.ReadAll(before);
            b = after is null ? [] : handler.ReadAll(after);
            if (a is not null && b is not null && (a.Count > 0 || b.Count > 0)) format = handler.Name;
            else { a = null; b = null; }
        }
        if (a is null || b is null)
        {
            var guessFrom = after ?? before!;
            if (KeyValueTextHandler.ReadAllGuessing(guessFrom, out var fmt) is not null && fmt is not null)
            {
                // the same line format for both sides, so a key is not "new" just because the guess differed
                a = before is null ? [] : KeyValueTextHandler.ReadAllWith(before, fmt);
                b = after is null ? [] : KeyValueTextHandler.ReadAllWith(after, fmt);
                format = "keyvalue";
            }
        }

        var changes = new List<KeyChange>();
        if (a is not null && b is not null)
        {
            var left = a.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var right = b.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            foreach (var (k, v) in b)
                if (!left.TryGetValue(k, out var old)) changes.Add(new KeyChange(k, null, v));
                else if (old != v) changes.Add(new KeyChange(k, old, v));
            foreach (var (k, v) in a)
                if (!right.ContainsKey(k)) changes.Add(new KeyChange(k, v, null));
        }
        else
        {
            format = "lines";
            changes.AddRange(LineChanges(before, after, limits));
            max = limits.MaxLineChanges;
        }

        // a pure move of text (same keys, same values) is no change at all
        var omitted = Math.Max(0, changes.Count - max);
        return (format, changes.Take(max).ToList(), omitted);
    }

    private static IEnumerable<KeyChange> LineChanges(string? before, string? after, ProbeLimits limits)
    {
        static Dictionary<string, int> Count(string? t)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (t is null) return d;
            foreach (var l in t.Split('\n'))
            {
                var line = l.Trim();
                if (line.Length == 0) continue;
                d[line] = d.GetValueOrDefault(line) + 1;
            }
            return d;
        }
        string Cut(string s) => s.Length > limits.MaxValueChars ? s[..limits.MaxValueChars] + "..." : s;
        var a = Count(before);
        var b = Count(after);
        foreach (var (line, n) in b)
            if (n > a.GetValueOrDefault(line)) yield return new KeyChange("(line)", null, Cut(line));
        foreach (var (line, n) in a)
            if (n > b.GetValueOrDefault(line)) yield return new KeyChange("(line)", Cut(line), null);
    }

    // ------------------------------------------------------------ evidence

    /// <summary>
    /// Pass A (launch, change nothing, quit) is what the game rewrites by itself: progress and state, never to be restored. Pass B (launch,
    /// change many graphics options, quit) shows where settings live: only the changes beyond pass A are listed as settings. The text is
    /// tokenized, sanitized and policy-checked like all lab evidence. <paramref name="passB"/> null = only pass A has run.
    /// </summary>
    public ProbeEvidence ToEvidence(ProbeDiff passA, ProbeDiff? passB)
    {
        var folders = new EvidenceCollector(_options).DiscoverProbeScope(_target).Folders;
        var notes = new List<string>();
        var stateLines = new List<string>();
        var settingsLines = new List<string>();
        var opaque = new List<string>();
        var readable = 0; // pass B changes that can be read key by key

        FileChange[] Allowed(IEnumerable<FileChange> files) => files.Where(f =>
        {
            if (EvidencePolicy.CheckPath(f.Path, folders, out _) is { } why) { notes.Add($"Left out {f.Path}: {why}."); return false; }
            return true;
        }).ToArray();

        var a = Allowed(passA.Files);
        var aByPath = a.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var aRegistry = passA.Registry.Select(r => (r.Key, r.Name)).ToHashSet(new RegistryKeyComparer());

        // ---- pass A
        var sbA = new StringBuilder();
        sbA.AppendLine("## Pass A: rewritten every session (progress/state, never restore)");
        sbA.AppendLine("The game was launched, nothing was changed, and it was quit. Everything below changed anyway.");
        if (a.Length == 0 && passA.Registry.Count == 0) sbA.AppendLine("(no files or registry values changed)");
        foreach (var f in a)
        {
            AppendFile(sbA, f, folders);
            stateLines.Add(Describe(f));
        }
        AppendRegistry(sbA, passA.Registry, null, out var aRegCount);
        if (aRegCount > 0) stateLines.Add($"{aRegCount} registry value(s) rewritten");

        // ---- pass B
        var sbB = new StringBuilder();
        sbB.AppendLine("## Pass B: settings changed (beyond pass A)");
        if (passB is null)
        {
            sbB.AppendLine("(pass B was not run)");
        }
        else
        {
            sbB.AppendLine("The game was launched again, many graphics options were changed in its menu, applied, and the game was quit.");
            var noise = 0;
            var both = new List<string>();
            var listed = 0;
            foreach (var f in Allowed(passB.Files))
            {
                if (aByPath.TryGetValue(f.Path, out var inA))
                {
                    if (f.IsText && inA.IsText && f.Kind == ProbeChangeKind.Changed)
                    {
                        // only the keys that pass A did not change as well
                        var seen = inA.Keys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
                        var fresh = f.Keys.Where(k => !seen.Contains(k.Key)).ToList();
                        noise += f.Keys.Count - fresh.Count;
                        if (fresh.Count == 0) { both.Add($"{f.Path} (text; only keys that also changed in pass A)"); continue; }
                        var trimmed = f with { Keys = fresh };
                        readable += fresh.Count;
                        AppendFile(sbB, trimmed, folders);
                        settingsLines.Add(Describe(trimmed));
                        listed++;
                        continue;
                    }
                    both.Add(f.IsText ? $"{f.Path} (text)" : $"{f.Path} (binary: changed in both passes, so settings cannot be told apart from state)");
                    continue;
                }
                AppendFile(sbB, f, folders);
                if (f.IsText && f.Kind != ProbeChangeKind.Removed) readable += Math.Max(1, f.Keys.Count + f.KeysOmitted);
                settingsLines.Add(Describe(f));
                listed++;
                if (f.IsBinary && f.Kind != ProbeChangeKind.Removed) opaque.Add(f.Path);
            }
            AppendRegistry(sbB, passB.Registry, aRegistry, out var bRegCount);
            if (bRegCount > 0) { settingsLines.Add($"{bRegCount} registry value(s) changed"); listed++; readable += bRegCount; }
            if (listed == 0) sbB.AppendLine("(nothing changed beyond pass A: the settings are not in the watched folders or registry keys)");
            if (both.Count > 0)
            {
                sbB.AppendLine("Changed in both passes (not settings evidence):");
                foreach (var b in both) sbB.AppendLine("  " + b);
            }
            if (noise > 0) sbB.AppendLine($"({noise} key change(s) in text files were left out because pass A changed them too)");
            if (opaque.Count > 0)
                sbB.AppendLine("Binary files changed only in pass B (settings may live in them; contents are never sent): " + string.Join("; ", opaque));
        }

        var header = new StringBuilder();
        header.AppendLine($"mapping run for {_target.Name} ({_target.Key})");
        header.AppendLine("pass A: launched, changed nothing, quit. pass B: launched, changed many graphics options, quit.");
        header.AppendLine($"limits: {_limits.MaxFiles} files, {_limits.MaxHashBytes / (1024 * 1024)} MB hashed, {_limits.MaxRegistryKeys} registry keys; binary contents are never sent, only size and hash.");
        var watched = (passA.Watched ?? []).Concat(passB?.Watched ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        header.AppendLine(watched.Count == 0 ? "watched folders: none found" : "watched folders: " + string.Join("; ", watched));
        foreach (var n in passA.Notes.Concat(passB?.Notes ?? []).Distinct()) { header.AppendLine("note: " + n); notes.Add(n); }

        string S(string t) => EvidenceSanitizer.Sanitize(t, folders, _options.UserProfile, _options.UserName, _options.MachineName);
        return new ProbeEvidence(S(header.ToString().TrimEnd()), S(sbA.ToString().TrimEnd()), S(sbB.ToString().TrimEnd()),
            stateLines.Select(S).ToList(), settingsLines.Select(S).ToList(), opaque.Select(S).ToList(), notes.Select(S).ToList(), readable, passB is not null);
    }

    private sealed class RegistryKeyComparer : IEqualityComparer<(string Key, string Name)>
    {
        public bool Equals((string Key, string Name) x, (string Key, string Name) y) =>
            x.Key.Equals(y.Key, StringComparison.OrdinalIgnoreCase) && x.Name.Equals(y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Name) o) =>
            HashCode.Combine(o.Key.ToLowerInvariant(), o.Name.ToLowerInvariant());
    }

    private static string Describe(FileChange f)
    {
        var name = Path.GetFileName(f.Path);
        var verb = f.Kind switch { ProbeChangeKind.Added => "new", ProbeChangeKind.Removed => "deleted", _ => "changed" };
        if (f.IsBinary) return $"{name}: {verb} (binary)";
        var n = f.Keys.Count + f.KeysOmitted;
        return f.Kind == ProbeChangeKind.Changed && n > 0 ? $"{name}: {(n == 1 ? "1 setting" : $"{n} settings")} {verb}" : $"{name}: {verb}";
    }

    private string Value(string? v)
    {
        if (v is null) return "";
        v = v.Replace("\r", "").Replace("\n", "\\n");
        return v.Length > _limits.MaxValueChars ? v[.._limits.MaxValueChars] + "..." : v;
    }

    private void AppendFile(StringBuilder sb, FileChange f, LabFolders folders)
    {
        var size = f.Kind switch
        {
            ProbeChangeKind.Added => $"{f.SizeAfter} bytes",
            ProbeChangeKind.Removed => $"{f.SizeBefore} bytes",
            _ => $"{f.SizeBefore} -> {f.SizeAfter} bytes",
        };
        var kind = f.Kind switch { ProbeChangeKind.Added => "new file", ProbeChangeKind.Removed => "deleted file", _ => "file" };
        if (f.IsBinary)
        {
            sb.AppendLine($"{kind} {f.Path}  (binary, {size}, sha256 {Short(f.HashBefore)} -> {Short(f.HashAfter)})");
            return;
        }
        sb.AppendLine($"{kind} {f.Path}  (text{(f.Format.Length > 0 ? ", " + f.Format : "")}, {size})");
        foreach (var k in f.Keys)
        {
            if (SecretKey.IsMatch(k.Key)) { sb.AppendLine($"  {k.Key}: [REDACTED]"); continue; }
            sb.AppendLine($"  {k.Key}: {(k.Before is null ? "(none)" : Value(k.Before))} -> {(k.After is null ? "(removed)" : Value(k.After))}");
        }
        if (f.KeysOmitted > 0) sb.AppendLine($"  ... {f.KeysOmitted} more change(s)");
    }

    private static string Short(string? hash) => hash is null ? "(not hashed)" : hash.Length > 12 ? hash[..12] : hash;

    private void AppendRegistry(StringBuilder sb, IReadOnlyList<RegistryChange> changes, HashSet<(string Key, string Name)>? skip, out int count)
    {
        count = 0;
        foreach (var group in changes.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.Where(c => skip is null || !skip.Contains((c.Key, c.Name))).ToList();
            if (rows.Count == 0) continue;
            sb.AppendLine($"registry {group.Key}");
            foreach (var c in rows)
            {
                count++;
                if (SecretKey.IsMatch(c.Name)) { sb.AppendLine($"  {c.Name}: [REDACTED]"); continue; }
                sb.AppendLine($"  {c.Name}: {(c.Before is null ? "(none)" : Value(c.Before))} -> {(c.After is null ? "(removed)" : Value(c.After))}");
            }
        }
    }

    private string Clean(string text, LabFolders folders) =>
        EvidenceSanitizer.Sanitize(text, folders, _options.UserProfile, _options.UserName, _options.MachineName);
}
