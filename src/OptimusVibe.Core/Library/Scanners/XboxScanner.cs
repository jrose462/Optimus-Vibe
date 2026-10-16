using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OptimusVibe.Logging;

namespace OptimusVibe.Library.Scanners;

/// <summary>
/// Xbox app / PC Game Pass (no PowerShell / WinRT needed). Packages are found two ways and only count when their
/// root holds a MicrosoftGame.config (so ordinary Store apps are ignored):
/// <list type="bullet">
/// <item>&lt;drive&gt;:\XboxGames\&lt;Game&gt;\Content\MicrosoftGame.config on every fixed drive (default Xbox app install folder);</item>
/// <item>HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\&lt;full name&gt;
/// (PackageRootFolder), which also covers custom install folders.</item>
/// </list>
/// StoreId = package family name (Identity Name + "_" + publisher hash). LaunchUri = shell:AppsFolder\&lt;PFN&gt;!&lt;AppId&gt;
/// (AppId from appxmanifest.xml's Application Id, else the first Executable Id in MicrosoftGame.config).
/// Name = ShellVisuals DefaultDisplayName (ms-resource: names fall back to the folder name). Art = a ShellVisuals logo.
/// </summary>
public sealed class XboxScanner : IGameScanner
{
    internal const string PackagesKey =
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private readonly IRegistryReader _registry;
    private readonly IReadOnlyList<string> _xboxGamesRoots;

    public XboxScanner() : this(WindowsRegistryReader.Instance,
        ScanUtil.FixedDrives().Select(d => Path.Combine(d, "XboxGames")).ToList()) { }

    internal XboxScanner(IRegistryReader registry, IReadOnlyList<string> xboxGamesRoots)
    {
        _registry = registry;
        _xboxGamesRoots = xboxGamesRoots;
    }

    public string Store => "xbox";

    public IEnumerable<DetectedGame> Scan()
    {
        // content folder → package full name (when known from the registry)
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var xg in _xboxGamesRoots)
        {
            foreach (var gameDir in ScanUtil.SafeDirs(xg))
            {
                var content = Path.Combine(gameDir, "Content");
                if (File.Exists(Path.Combine(content, "MicrosoftGame.config"))) roots.TryAdd(Path.GetFullPath(content), null);
            }
        }

        try
        {
            foreach (var fullName in _registry.SubKeys(PackagesKey))
            {
                var root = ScanUtil.NormalizeDir(_registry.Value($@"{PackagesKey}\{fullName}", "PackageRootFolder"));
                if (root is null || !File.Exists(Path.Combine(root, "MicrosoftGame.config"))) continue;
                roots[root] = fullName;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Xbox: could not read the package repository: {ex.Message}");
        }

        var result = new List<DetectedGame>();
        foreach (var (root, fullName) in roots)
        {
            try
            {
                var game = Read(root, fullName);
                if (game is not null) result.Add(game);
            }
            catch (Exception ex)
            {
                Log.Warn($"Xbox: skipped {root}: {ex.Message}");
            }
        }
        return result.DistinctBy(g => g.Key).ToList();
    }

    private DetectedGame? Read(string contentDir, string? fullName)
    {
        var config = XDocument.Load(Path.Combine(contentDir, "MicrosoftGame.config")).Root!;
        XElement? First(XElement scope, string name) => scope.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

        XElement? appx = null;
        var appxPath = Path.Combine(contentDir, "appxmanifest.xml");
        if (File.Exists(appxPath))
        {
            try { appx = XDocument.Load(appxPath).Root; }
            catch (Exception ex) { Log.Warn($"Xbox: unreadable {appxPath}: {ex.Message}"); }
        }

        var identity = First(config, "Identity") ?? (appx is null ? null : First(appx, "Identity"));
        var identityName = (string?)identity?.Attribute("Name");
        var publisher = (string?)identity?.Attribute("Publisher");

        string? pfn = null;
        if (fullName is not null)
        {
            var parts = fullName.Split('_');
            if (parts.Length >= 2) pfn = $"{parts[0]}_{parts[^1]}";
        }
        if (pfn is null && identityName is not null && publisher is not null) pfn = $"{identityName}_{PublisherHash(publisher)}";
        if (pfn is null) return null;

        var appId = (string?)(appx is null ? null : First(appx, "Application"))?.Attribute("Id")
                    ?? (string?)First(config, "Executable")?.Attribute("Id")
                    ?? "Game";

        var gameFolder = Path.GetFileName(Path.GetDirectoryName(contentDir)) is { Length: > 0 } parent
                         && Path.GetFileName(contentDir).Equals("Content", StringComparison.OrdinalIgnoreCase)
            ? parent
            : Path.GetFileName(contentDir);

        var visuals = First(config, "ShellVisuals");
        var name = (string?)visuals?.Attribute("DefaultDisplayName");
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            var appxName = appx is null ? null : First(appx, "DisplayName")?.Value;
            name = !string.IsNullOrWhiteSpace(appxName) && !appxName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                ? appxName
                : gameFolder;
        }

        string? art = null;
        foreach (var attr in new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo", "Square44x44Logo" })
        {
            art = ResolveAsset(contentDir, (string?)visuals?.Attribute(attr));
            if (art is not null) break;
        }

        var exes = config.Descendants().Where(e => e.Name.LocalName == "Executable")
            .Select(e => (string?)e.Attribute("Name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => Path.GetFileName(n!.Replace('/', '\\')))
            .ToList();

        return new DetectedGame
        {
            Store = Store,
            StoreId = pfn,
            Name = name.Trim(),
            InstallDir = contentDir,
            LaunchUri = $@"shell:AppsFolder\{pfn}!{appId}",
            ArtPath = art,
            Exes = ScanUtil.FindExes(contentDir, exes),
        };
    }

    /// <summary>A logo path from the config; MSIX assets are often stored with a scale qualifier (Logo.scale-200.png).</summary>
    private static string? ResolveAsset(string contentDir, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = Path.Combine(contentDir, relative.Replace('/', '\\'));
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return ScanUtil.SafeFiles(dir, $"{stem}.*{ext}")
            .OrderByDescending(f => f.Contains("scale-", StringComparison.OrdinalIgnoreCase) ? ScaleOf(f) : 0)
            .FirstOrDefault();
    }

    private static int ScaleOf(string file)
    {
        var i = file.IndexOf("scale-", StringComparison.OrdinalIgnoreCase);
        var digits = new string(file[(i + 6)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var s) ? s : 0;
    }

    /// <summary>The 13-char publisher id of a package family name: Crockford-ish base32 of the first 8 bytes of SHA-256(UTF-16LE publisher).</summary>
    internal static string PublisherHash(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (int i = 0; i < 8; i++) bits = (bits << 8) | hash[i];
        var sb = new StringBuilder(13);
        // 64 bits padded with one zero bit to 65 = 13 groups of 5.
        for (int i = 0; i < 13; i++)
        {
            int shift = 59 - 5 * i;
            int idx = shift >= 0 ? (int)((bits >> shift) & 0x1F) : (int)((bits << -shift) & 0x1F);
            sb.Append(alphabet[idx]);
        }
        return sb.ToString();
    }
}
