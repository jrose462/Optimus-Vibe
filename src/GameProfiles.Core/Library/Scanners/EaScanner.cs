using System.Xml.Linq;
using GameProfiles.Logging;

namespace GameProfiles.Library.Scanners;

/// <summary>
/// EA app (and legacy Origin) installs. Candidate folders come from:
/// <list type="bullet">
/// <item>HKLM Uninstall entries published by Electronic Arts (minus the EA app / Origin themselves);</item>
/// <item>HKLM\SOFTWARE[\WOW6432Node]\{Electronic Arts, EA Games, Respawn}\&lt;game&gt; "Install Dir" values;</item>
/// <item>the default library folders &lt;drive&gt;:\Program Files\EA Games\* (and \Origin Games\*).</item>
/// </list>
/// A folder only counts when it holds __Installer\installerdata.xml (or installerdata.xml) — the EA installer manifest
/// that every EA app game has — which gives contentIDs, the title and the game exe. Installs inside a Steam library
/// (EA games bought on Steam register here too) are skipped; the Steam scanner owns them.
/// StoreId = first contentID (else the folder name). Launch: LaunchExe = the game exe from installerdata's
/// &lt;runtime&gt;&lt;launcher&gt;&lt;filePath&gt;; starting the exe hands off to the EA app for sign-in/DRM, which is reliable
/// across EA app versions (origin2:// / link2ea:// URIs need an offer id that installerdata does not record).
/// C:\ProgramData\EA Desktop\InstallData is read when present (one folder per installed game slug; its
/// installerdata.xml copies are parsed the same way).
/// </summary>
public sealed class EaScanner : IGameScanner
{
    private static readonly string[] RegistryRoots =
    [
        @"HKLM\SOFTWARE\WOW6432Node\Electronic Arts", @"HKLM\SOFTWARE\WOW6432Node\EA Games", @"HKLM\SOFTWARE\WOW6432Node\Respawn",
        @"HKLM\SOFTWARE\Electronic Arts", @"HKLM\SOFTWARE\EA Games", @"HKLM\SOFTWARE\Respawn",
    ];

    private static readonly string[] NotGames = ["EA app", "EA Desktop", "Origin", "EA Core", "EADM", "EA Desktop Launcher"];

    private readonly IRegistryReader _registry;
    private readonly IReadOnlyList<string> _libraryRoots;
    private readonly string? _installDataDir;

    public EaScanner() : this(WindowsRegistryReader.Instance, DefaultLibraryRoots(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EA Desktop", "InstallData")) { }

    internal EaScanner(IRegistryReader registry, IReadOnlyList<string> libraryRoots, string? installDataDir)
    {
        _registry = registry;
        _libraryRoots = libraryRoots;
        _installDataDir = installDataDir;
    }

    private static IReadOnlyList<string> DefaultLibraryRoots() =>
        ScanUtil.FixedDrives()
            .SelectMany(d => new[]
            {
                Path.Combine(d, "Program Files", "EA Games"), Path.Combine(d, "Program Files (x86)", "EA Games"),
                Path.Combine(d, "Program Files (x86)", "Origin Games"), Path.Combine(d, "EA Games"), Path.Combine(d, "Origin Games"),
            })
            .ToList();

    public string Store => "ea";

    public IEnumerable<DetectedGame> Scan()
    {
        // install dir → display name hint
        var candidates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Add(string? dir, string? name)
        {
            var d = ScanUtil.NormalizeDir(dir);
            if (d is null) return;
            if (!candidates.TryGetValue(d, out var existing) || existing is null) candidates[d] = name;
        }

        try
        {
            foreach (var e in UninstallEntry.Read(_registry))
            {
                if (e.Publisher?.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) != true) continue;
                if (NotGames.Any(n => string.Equals(n, e.DisplayName, StringComparison.OrdinalIgnoreCase))) continue;
                Add(e.InstallLocation, e.DisplayName);
            }
            foreach (var root in RegistryRoots)
            foreach (var sub in _registry.SubKeys(root))
            {
                if (NotGames.Contains(sub, StringComparer.OrdinalIgnoreCase)) continue;
                var key = $@"{root}\{sub}";
                Add(_registry.Value(key, "Install Dir") ?? _registry.Value(key, "InstallDir"), _registry.Value(key, "DisplayName"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"EA: could not read the registry: {ex.Message}");
        }

        foreach (var root in _libraryRoots)
            foreach (var dir in ScanUtil.SafeDirs(root))
                Add(dir, null);

        var result = new List<DetectedGame>();
        foreach (var (dir, nameHint) in candidates)
        {
            try
            {
                if (dir.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.Exists(dir)) continue;
                var manifest = FindInstallerData(dir);
                if (manifest is null) continue;
                var game = Read(dir, manifest, nameHint);
                if (game is not null) result.Add(game);
            }
            catch (Exception ex)
            {
                Log.Warn($"EA: skipped {dir}: {ex.Message}");
            }
        }

        // EA app's own install records, for games whose folder we did not reach otherwise.
        if (_installDataDir is not null && Directory.Exists(_installDataDir))
        {
            foreach (var xml in ScanUtil.SafeFiles(_installDataDir, "installerdata.xml", SearchOption.AllDirectories))
            {
                try
                {
                    var info = InstallerData.Parse(XDocument.Load(xml));
                    if (info.ContentId is null || result.Any(r => r.StoreId == info.ContentId)) continue;
                    var dir = ResolveRegistryPath(info.ExeRegistryBase);
                    if (dir is null || !Directory.Exists(dir) || dir.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) continue;
                    var game = Read(dir, xml, null);
                    if (game is not null) result.Add(game);
                }
                catch (Exception ex)
                {
                    Log.Warn($"EA: skipped {xml}: {ex.Message}");
                }
            }
        }
        return result.DistinctBy(g => g.Key).ToList();
    }

    private static string? FindInstallerData(string dir)
    {
        foreach (var p in new[] { Path.Combine(dir, "__Installer", "installerdata.xml"), Path.Combine(dir, "installerdata.xml") })
            if (File.Exists(p)) return p;
        return null;
    }

    private DetectedGame? Read(string dir, string manifest, string? nameHint)
    {
        var info = InstallerData.Parse(XDocument.Load(manifest));
        var exe = info.ExeRelative is null ? null : Path.Combine(dir, info.ExeRelative.TrimStart('\\', '/'));
        if (exe is not null && !File.Exists(exe)) exe = null;
        var name = info.Title ?? nameHint ?? Path.GetFileName(dir);
        return new DetectedGame
        {
            Store = Store,
            StoreId = info.ContentId ?? Path.GetFileName(dir),
            Name = name,
            InstallDir = dir,
            LaunchExe = exe,
            LaunchArgs = ScanUtil.SplitArgs(info.Parameters),
            Exes = ScanUtil.FindExes(dir, exe is null ? null : [Path.GetFileName(exe)]),
        };
    }

    /// <summary>"[HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Jedi Survivor\Install Dir]" → the folder it names.</summary>
    private string? ResolveRegistryPath(string? bracket)
    {
        if (bracket is null) return null;
        var inner = bracket.Trim('[', ']');
        var slash = inner.LastIndexOf('\\');
        if (slash < 0) return null;
        var key = inner[..slash].Replace("HKEY_LOCAL_MACHINE", "HKLM", StringComparison.OrdinalIgnoreCase);
        var name = inner[(slash + 1)..];
        var value = _registry.Value(key, name);
        if (value is null && key.StartsWith(@"HKLM\SOFTWARE\", StringComparison.OrdinalIgnoreCase) && !key.Contains("WOW6432Node"))
            value = _registry.Value(@"HKLM\SOFTWARE\WOW6432Node\" + key[@"HKLM\SOFTWARE\".Length..], name);
        return ScanUtil.NormalizeDir(value);
    }

    /// <summary>The bits of an EA installerdata.xml (DiPManifest) the library needs.</summary>
    internal sealed record InstallerData(string? ContentId, string? Title, string? ExeRelative, string? ExeRegistryBase, string? Parameters)
    {
        public static InstallerData Parse(XDocument doc)
        {
            var root = doc.Root!;
            IEnumerable<XElement> All(string name) => root.Descendants().Where(e => e.Name.LocalName == name);

            var contentId = All("contentID").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            string? Localized(IEnumerable<XElement> items) =>
                (items.FirstOrDefault(e => (string?)e.Attribute("locale") == "en_US") ?? items.FirstOrDefault())?.Value.Trim();

            var launcher = All("launcher").FirstOrDefault(l => !string.Equals((string?)l.Element(l.Name.Namespace + "trial"), "1"));
            var title = Localized(launcher?.Elements().Where(e => e.Name.LocalName == "name") ?? [])
                        ?? Localized(All("gameTitle"));

            string? exeRel = null, regBase = null;
            var filePath = launcher?.Elements().FirstOrDefault(e => e.Name.LocalName == "filePath")?.Value.Trim();
            if (!string.IsNullOrEmpty(filePath))
            {
                var close = filePath.IndexOf(']');
                if (filePath.StartsWith('[') && close > 0)
                {
                    regBase = filePath[..(close + 1)];
                    exeRel = filePath[(close + 1)..];
                }
                else exeRel = filePath;
            }
            var parameters = launcher?.Elements().FirstOrDefault(e => e.Name.LocalName == "parameters")?.Value.Trim();
            return new InstallerData(contentId, string.IsNullOrEmpty(title) ? null : title, exeRel, regBase,
                string.IsNullOrEmpty(parameters) ? null : parameters);
        }
    }
}
