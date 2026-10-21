using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OptimusVibe.Config;
using OptimusVibe.Library;
using OptimusVibe.Library.Scanners;
using OptimusVibe.Logging;

namespace OptimusVibe.GameLab;

/// <summary>The game a request is about: what the app knows from the launcher scan and/or the config repo.</summary>
public sealed record LabTarget
{
    public required string Name { get; init; }
    public required string Store { get; init; }
    public required string StoreId { get; init; }
    public string? InstallDir { get; init; }
    public IReadOnlyList<string> Exes { get; init; } = [];

    /// <summary>Id of the existing games\&lt;id&gt;.yaml, if the game is configured.</summary>
    public string? GameId { get; init; }

    /// <summary>Publisher / developer name when known (otherwise read from the game exe's version info).</summary>
    public string? Publisher { get; init; }

    public GameDef? Game { get; init; }
    public DetectedGame? Detected { get; init; }

    public string Key => $"{Store}:{StoreId}";

    public static LabTarget From(LibraryEntry entry) => From(entry.Detected, entry.Game, entry.Name);

    public static LabTarget From(DetectedGame? detected, GameDef? game, string? name = null)
    {
        var store = detected?.Store ?? game?.Store ?? "other";
        var storeId = detected?.StoreId
                      ?? (game is null ? "" : game.SteamAppId?.ToString() ?? game.StoreIds.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "");
        if (detected is null && game is { SteamAppId: not null }) store = "steam";
        return new LabTarget
        {
            Name = name ?? game?.Name ?? detected?.Name ?? game?.Id ?? "",
            Store = store,
            StoreId = storeId,
            InstallDir = detected?.InstallDir,
            Exes = detected?.Exes.Count > 0 ? detected.Exes : game?.Processes ?? [],
            GameId = game?.Id,
            Game = game,
            Detected = detected,
        };
    }
}

/// <summary>Injectable pieces of <see cref="EvidenceCollector"/> (tests use a fake profile tree and registry).</summary>
public sealed class EvidenceOptions
{
    public LabFolders Folders { get; init; } = LabFolders.FromSystem();
    public IRegistryView Registry { get; init; } = new SystemRegistryView();

    /// <summary>Text for the "Cloud check" section (the sync report and Steam Cloud listing); null/empty = no section.</summary>
    public Func<LabTarget, string?>? CloudCheck { get; init; }

    /// <summary>Contents of the existing games\&lt;id&gt;.yaml for "Current config".</summary>
    public Func<LabTarget, string?>? CurrentConfig { get; init; }

    /// <summary>
    /// Text for the "Verify run" section (what a test of the preset found, see <see cref="Launch.RunResults.EvidenceText"/>); null/empty = no
    /// section. Sent with a Refresh request after the player tested a preset.
    /// </summary>
    public Func<LabTarget, string?>? VerifyRun { get; init; }

    /// <summary>Overrides for sanitizing (tests); default: the real user / machine / profile.</summary>
    public string? UserProfile { get; init; }
    public string? UserName { get; init; }
    public string? MachineName { get; init; }

    public int MaxCandidateFiles { get; init; } = 60;
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024;
    public int MaxTextBytes { get; init; } = 200 * 1024;
    public int MaxBinaryStringChars { get; init; } = 4096;
    public int MaxListingEntries { get; init; } = 300;

    /// <summary>All file contents together (characters); beyond this files are listed in the notes only.</summary>
    public int MaxTotalFileChars { get; init; } = 600_000;
}

/// <summary>
/// Collects, read-only and never throwing, the evidence the lab needs to research a game: install facts, candidate settings
/// folders and their files, directory listings, registry values, the cloud-sync report and the current config. Output is
/// tokenized ({LocalAppData}...), redacted and size-capped; skipped or failed items are explained in <see cref="EvidenceBundle.Notes"/>.
/// </summary>
public sealed class EvidenceCollector(EvidenceOptions? options = null)
{
    private readonly EvidenceOptions _o = options ?? new EvidenceOptions();

    private static readonly string[] TextExtensions = [".ini", ".cfg", ".conf", ".json", ".xml", ".lsx", ".txt", ".sav", ".wtf", ".yaml", ".yml"];

    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "savegames", "saves", "save", "saved games", "logs", "log", "crashes", "crashreportclient", "crashreports", "crash", "webcache", "cache",
        "shadercache", "gpucache", "code cache", "dxcache", "screenshots", "videos", "movies", "backup", "backups", "telemetry", "temp", "tmp",
        "autosave", "autosaves", "cloudsaves", "persistentdownloaddir", "d3dscache", "glcache", "blob_storage", "crashpad", "mods", "workshop",
    };

    private static readonly Regex SaveLike = new(@"(?i)(save|profile\d|slot\d|autosave|quicksave|checkpoint)", RegexOptions.Compiled);
    private static readonly Regex SettingsLike = new(@"(?i)(setting|config|option|pref|graphic|video|display|input|keybind|scalab|engine|gameuser|user|device|quality|hardware)", RegexOptions.Compiled);
    private static readonly Regex NoiseFile = new(@"(?i)^(license|readme|thirdparty|third-party|eula|credits|changelog|notice|manifest|appxmanifest|steam_appid|version|history|release.?notes)", RegexOptions.Compiled);
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "epic", "games", "game", "unity", "unreal", "microsoft", "nvidia", "amd", "intel", "temp", "cache", "packages", "programs", "apps",
        "windows", "common", "data", "config", "settings", "software", "saved", "users", "user",
    };

    // ------------------------------------------------------------ collect

    /// <summary>Evidence for a request. Never throws.</summary>
    public EvidenceBundle Collect(LabTarget target)
    {
        var bundle = new EvidenceBundle();
        try
        {
            CollectCore(target, bundle);
        }
        catch (Exception ex)
        {
            Log.Warn($"game lab: evidence collection for {target.Name} failed: {ex.Message}");
            bundle.Notes.Add($"Evidence collection stopped early: {Clean(ex.Message)}");
        }
        return bundle;
    }

    private LabFolders FoldersFor(LabTarget t)
    {
        var f = _o.Folders;
        if (!string.IsNullOrWhiteSpace(t.InstallDir))
        {
            if (t.Store.Equals("steam", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.StoreId, out _))
                f = f.WithExtra($"SteamLibrary:{t.StoreId}", t.InstallDir); // first: wins the tie when tokenizing
            f = f.WithExtra("InstallDir", t.InstallDir);
        }
        return f;
    }

    private void CollectCore(LabTarget t, EvidenceBundle bundle)
    {
        var folders = FoldersFor(t);
        var install = SafeDir(t.InstallDir);

        // --- install facts
        var engine = new EngineHints();
        if (install is not null) engine = DetectEngine(install);
        var publisher = t.Publisher ?? DetectPublisher(install, t.Exes);
        bundle.Sections.Add(InstallSection(t, install, engine, publisher, folders));

        // --- the stores the current config already maps (files, registry keys): always read, before any guessing, so a
        // refresh can confirm the keys and values it has. Same policy checks as a needs-evidence follow-up.
        if (MappedStores(t) is { IsEmpty: false } mapped)
        {
            var known = CollectRequested(mapped, t);
            bundle.Sections.AddRange(known.Sections);
            bundle.Notes.AddRange(known.Notes.Select(n => $"Mapped store: {n}"));
        }

        // --- candidate settings stores
        var keys = NameKeys(t, engine);
        var pubKeys = PublisherKeys(publisher);
        var candidates = FindCandidateFolders(folders, keys, pubKeys, bundle);
        var files = new List<CandidateFile>();
        foreach (var dir in candidates) files.AddRange(FilesIn(dir, bundle));
        if (install is not null) files.AddRange(InstallConfigFiles(install));

        // Pick the best N, then output sorted by path for a stable layout.
        var picked = files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(_o.MaxCandidateFiles).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > picked.Count) bundle.Notes.Add($"{files.Count - picked.Count} more candidate file(s) were not included (limit {_o.MaxCandidateFiles}).");

        var chars = 0;
        foreach (var f in picked)
        {
            if (chars >= _o.MaxTotalFileChars)
            {
                bundle.Notes.Add($"File budget reached: not included: {folders.Tokenize(f.Path)}");
                continue;
            }
            var section = ReadFileSection(f.Path, folders, bundle);
            if (section is null) continue;
            if (bundle.Sections.Any(x => x.Kind == EvidenceKind.File && x.Title == section.Title)) continue; // already read as a mapped store
            chars += section.Body.Length;
            bundle.Sections.Add(section);
        }

        // --- directory listings
        foreach (var dir in candidates)
            AddListing(bundle, folders, dir);
        if (install is not null) AddListing(bundle, folders, install);

        // --- registry
        CollectRegistry(keys, pubKeys, bundle, folders);

        // --- cloud check + current config
        TryAdd(bundle, "cloud check", () =>
        {
            var text = _o.CloudCheck?.Invoke(t);
            if (!string.IsNullOrWhiteSpace(text))
                bundle.Sections.Add(new EvidenceSection(EvidenceKind.CloudCheck, "", Clean(text, folders)));
        });
        TryAdd(bundle, "verify run", () =>
        {
            var text = _o.VerifyRun?.Invoke(t);
            if (!string.IsNullOrWhiteSpace(text))
                bundle.Sections.Add(new EvidenceSection(EvidenceKind.VerifyRun, "", Clean(text, folders)));
        });
        TryAdd(bundle, "current config", () =>
        {
            var yaml = _o.CurrentConfig?.Invoke(t);
            if (!string.IsNullOrWhiteSpace(yaml))
                bundle.Sections.Add(new EvidenceSection(EvidenceKind.CurrentConfig, "", Clean(yaml, folders)));
        });
    }

    /// <summary>The files and registry keys listed under <c>files:</c> in the game's current config; null when there is none.</summary>
    private NeedsEvidence? MappedStores(LabTarget t)
    {
        try
        {
            var yaml = _o.CurrentConfig?.Invoke(t);
            if (string.IsNullOrWhiteSpace(yaml)) return null;
            var def = Yaml.Parse<GameDef>(yaml);
            var files = def.Files.Where(f => !f.Handler.Equals("registry", StringComparison.OrdinalIgnoreCase) && f.Path.Length > 0)
                .Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keys = def.Files.Where(f => f.Handler.Equals("registry", StringComparison.OrdinalIgnoreCase) && f.Path.Length > 0)
                .Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new NeedsEvidence(files, keys, []);
        }
        catch (Exception ex)
        {
            Log.Warn($"game lab: could not read the mapped stores of {t.Name}: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------ follow-ups (needs-evidence)

    /// <summary>
    /// Collects exactly what a needs-evidence comment asked for, after <see cref="EvidencePolicy"/> checks. Refused or empty
    /// requests are explained in the notes. Never throws.
    /// </summary>
    public EvidenceBundle CollectRequested(NeedsEvidence request, LabTarget? target = null)
    {
        var bundle = new EvidenceBundle();
        try
        {
            var folders = target is null ? _o.Folders : FoldersFor(target);
            var budget = 0;

            foreach (var pattern in request.Files.Take(EvidencePolicy.MaxFilePatterns))
            {
                if (EvidencePolicy.CheckPath(pattern, folders, out var abs) is { } why)
                {
                    bundle.Notes.Add($"Refused file request '{Clean(pattern, folders)}': {why}.");
                    continue;
                }
                var matches = ExpandGlob(abs, EvidencePolicy.MaxMatchesPerPattern);
                if (matches.Count == 0) { bundle.Notes.Add($"No file matched '{Clean(pattern, folders)}'."); continue; }
                foreach (var file in matches)
                {
                    if (bundle.Count(EvidenceKind.File) >= EvidencePolicy.MaxFilesTotal || budget >= _o.MaxTotalFileChars)
                    {
                        bundle.Notes.Add($"Limit reached; not included: {Clean(folders.Tokenize(file), folders)}");
                        continue;
                    }
                    // Re-check the concrete file: wildcards could have walked into a protected folder.
                    if (EvidencePolicy.CheckPath(folders.Tokenize(file), folders, out _) is { } why2)
                    {
                        bundle.Notes.Add($"Skipped {Clean(folders.Tokenize(file), folders)}: {why2}.");
                        continue;
                    }
                    var s = ReadFileSection(file, folders, bundle);
                    if (s is null) continue;
                    if (bundle.Sections.Any(x => x.Kind == EvidenceKind.File && x.Title == s.Title)) continue;
                    budget += s.Body.Length;
                    bundle.Sections.Add(s);
                }
            }
            if (request.Files.Count > EvidencePolicy.MaxFilePatterns) bundle.Notes.Add($"Only the first {EvidencePolicy.MaxFilePatterns} file patterns were used.");

            foreach (var key in request.Registry.Take(EvidencePolicy.MaxRegistryKeys))
            {
                if (EvidencePolicy.CheckRegistryKey(key, out var norm) is { } why)
                {
                    bundle.Notes.Add($"Refused registry request '{Clean(key, folders)}': {why}.");
                    continue;
                }
                var before = bundle.Sections.Count;
                AddRegistryKey(bundle, norm, depth: 1, folders);
                if (bundle.Sections.Count == before) bundle.Notes.Add($"Registry key {norm} does not exist or has no values.");
            }

            foreach (var dir in request.ListDirs.Take(EvidencePolicy.MaxListDirs))
            {
                if (EvidencePolicy.CheckPath(dir, folders, out var abs) is { } why)
                {
                    bundle.Notes.Add($"Refused directory request '{Clean(dir, folders)}': {why}.");
                    continue;
                }
                if (abs.Contains('*') || abs.Contains('?')) { bundle.Notes.Add($"Directory request '{Clean(dir, folders)}' has wildcards; name one folder."); continue; }
                if (!Directory.Exists(abs)) { bundle.Notes.Add($"Directory '{Clean(dir, folders)}' does not exist."); continue; }
                AddListing(bundle, folders, abs);
            }

            if (bundle.Sections.Count == 0 && bundle.Notes.Count == 0)
                bundle.Notes.Add("The request asked for nothing.");
        }
        catch (Exception ex)
        {
            Log.Warn($"game lab: answering a needs-evidence request failed: {ex.Message}");
            bundle.Notes.Add($"Collecting the requested evidence stopped early: {Clean(ex.Message)}");
        }
        return bundle;
    }

    // ------------------------------------------------------------ install

    private sealed record EngineHints(string? Engine = null, string? UnrealProject = null, string? UnityData = null);

    private static EngineHints DetectEngine(string install)
    {
        string? unreal = null, unity = null;
        try
        {
            foreach (var d1 in SafeDirs(install))
            {
                var name = Path.GetFileName(d1);
                if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(Path.Combine(d1, "globalgamemanagers")) || File.Exists(Path.Combine(d1, "data.unity3d")) || File.Exists(Path.Combine(d1, "level0"))))
                    unity ??= name;
                if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(Path.Combine(d1, "Binaries", "Win64")) || Directory.Exists(Path.Combine(d1, "Binaries", "WinGDK")))
                    unreal ??= name;
            }
            // Some games nest the project one level deeper (install\Game\ProjectName\Binaries\Win64).
            if (unreal is null)
                foreach (var d1 in SafeDirs(install))
                    foreach (var d2 in SafeDirs(d1))
                        if (!Path.GetFileName(d2).Equals("Engine", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d2, "Binaries", "Win64")))
                        { unreal ??= Path.GetFileName(d2); break; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (File.Exists(Path.Combine(install, "UnityPlayer.dll"))) unity ??= "(UnityPlayer.dll)";
        return unreal is not null ? new("Unreal Engine", unreal, null)
            : unity is not null ? new("Unity", null, unity)
            : new();
    }

    private static string? DetectPublisher(string? install, IReadOnlyList<string> exes)
    {
        if (install is null) return null;
        try
        {
            var candidates = new List<string>();
            foreach (var e in exes.Take(3))
                candidates.AddRange(Directory.EnumerateFiles(install, e, SearchOption.AllDirectories).Take(2));
            if (candidates.Count == 0) candidates.AddRange(Directory.EnumerateFiles(install, "*.exe", SearchOption.TopDirectoryOnly).Take(3));
            foreach (var c in candidates)
            {
                var company = FileVersionInfo.GetVersionInfo(c).CompanyName;
                if (!string.IsNullOrWhiteSpace(company) && company.Trim().Length >= 3 && !company.Contains("Epic Games", StringComparison.OrdinalIgnoreCase))
                    return company.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private EvidenceSection InstallSection(LabTarget t, string? install, EngineHints engine, string? publisher, LabFolders folders)
    {
        var sb = new StringBuilder();
        sb.Append("name: ").Append(t.Name).Append('\n');
        sb.Append("store: ").Append(t.Store).Append("  id: ").Append(t.StoreId).Append('\n');
        if (!string.IsNullOrEmpty(t.GameId)) sb.Append("config id: ").Append(t.GameId).Append('\n');
        if (!string.IsNullOrWhiteSpace(publisher)) sb.Append("publisher (exe version info): ").Append(publisher).Append('\n');
        if (engine.Engine is not null)
            sb.Append("engine: ").Append(engine.Engine)
              .Append(engine.UnrealProject is not null ? $" (project folder {engine.UnrealProject})" : engine.UnityData is not null ? $" ({engine.UnityData})" : "")
              .Append('\n');
        if (install is null)
        {
            sb.Append("install folder: not found on this PC\n");
        }
        else
        {
            var exeList = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(install, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (ScanUtil.IsHelperExe(Path.GetFileName(f))) continue;
                    exeList.Add(Path.GetRelativePath(install, f) + $"  ({new FileInfo(f).Length} B)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            exeList.Sort(StringComparer.OrdinalIgnoreCase);
            sb.Append("executables:\n");
            foreach (var e in exeList.Take(40)) sb.Append("  ").Append(e).Append('\n');
            if (exeList.Count > 40) sb.Append($"  ... {exeList.Count - 40} more\n");
            if (t.Exes.Count > 0) sb.Append("launcher-reported exes: ").Append(string.Join(", ", t.Exes)).Append('\n');
        }
        var title = install is null ? "(not installed)" : folders.Tokenize(install);
        return new EvidenceSection(EvidenceKind.Install, Clean(title, folders), Clean(sb.ToString(), folders));
    }

    // ------------------------------------------------------------ discovery

    /// <summary>Lower-case alphanumerics only.</summary>
    internal static string Normalize(string? s) => Regex.Replace((s ?? "").ToLowerInvariant().Replace("&", "and"), @"[^a-z0-9]", "");

    private HashSet<string> NameKeys(LabTarget t, EngineHints engine)
    {
        var raw = new List<string?> { t.Name, t.Game?.Name, t.GameId };
        // "Name: Subtitle" / "Name - Subtitle" and without trademark marks or "edition" tails.
        foreach (var n in new[] { t.Name, t.Game?.Name })
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            raw.Add(Regex.Split(n, @"\s[-:]\s|:\s")[0]);
            raw.Add(Regex.Replace(n, @"(?i)\b(game of the year|definitive|remastered|remaster|complete|deluxe|ultimate|standard|gold|goty)\b.*$|\((tm|r)\)|[™®]", "").Trim());
        }
        if (!string.IsNullOrEmpty(t.InstallDir)) raw.Add(Path.GetFileName(t.InstallDir.TrimEnd('\\', '/')));
        raw.Add(engine.UnrealProject);
        foreach (var e in t.Exes)
            raw.Add(Regex.Replace(Path.GetFileNameWithoutExtension(e), @"(?i)[-_ ](win(64|32)|shipping|x64|x86|launcher|dx1[12]|vulkan)\b", "", RegexOptions.None));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in raw)
        {
            var k = Normalize(r);
            if (k.Length >= 4 && !GenericNames.Contains(k)) result.Add(k);
        }
        return result;
    }

    private static HashSet<string> PublisherKeys(string? publisher)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(publisher)) return result;
        var full = Normalize(publisher);
        if (full.Length >= 3) result.Add(full);
        var stripped = Normalize(Regex.Replace(publisher, @"(?i)\b(inc|llc|ltd|limited|corp|corporation|gmbh|co|s\.?a\.?|entertainment|interactive|studios?|games?|software|publishing|productions?)\b\.?", ""));
        if (stripped.Length >= 3) result.Add(stripped);
        return result;
    }

    private static bool Matches(string folderName, HashSet<string> keys)
    {
        var n = Normalize(folderName);
        if (n.Length < 3) return false;
        foreach (var k in keys)
        {
            if (n == k) return true;
            if (k.Length >= 5 && n.Length >= 5 && !GenericNames.Contains(n) && (n.Contains(k, StringComparison.Ordinal) || k.Contains(n, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private List<string> FindCandidateFolders(LabFolders folders, HashSet<string> gameKeys, HashSet<string> pubKeys, EvidenceBundle bundle)
    {
        var found = new List<string>();
        if (gameKeys.Count == 0 && pubKeys.Count == 0) { bundle.Notes.Add("No usable name to look for settings folders with."); return found; }

        void Scan(string? root, bool level2All)
        {
            if (root is null || !Directory.Exists(root)) return;
            foreach (var d1 in SafeDirs(root))
            {
                var n1 = Path.GetFileName(d1);
                if (n1.Equals("My Games", StringComparison.OrdinalIgnoreCase)) continue; // handled by the caller
                if (Matches(n1, gameKeys)) { found.Add(d1); continue; }
                if (!level2All && !(pubKeys.Count > 0 && Matches(n1, pubKeys))) continue;
                foreach (var d2 in SafeDirs(d1))
                    if (Matches(Path.GetFileName(d2), gameKeys)) found.Add(d2);
            }
        }

        var docs = folders.Get("Documents");
        if (docs is not null)
        {
            Scan(docs, level2All: false);
            var myGames = Path.Combine(docs, "My Games");
            if (Directory.Exists(myGames))
                foreach (var d in SafeDirs(myGames))
                    if (Matches(Path.GetFileName(d), gameKeys)) found.Add(d);
                    else if (pubKeys.Count > 0 && Matches(Path.GetFileName(d), pubKeys))
                        foreach (var d2 in SafeDirs(d)) if (Matches(Path.GetFileName(d2), gameKeys)) found.Add(d2);
        }
        Scan(folders.Get("LocalAppData"), level2All: false);
        Scan(folders.Get("AppData"), level2All: false);
        Scan(folders.Get("LocalLow"), level2All: true);
        Scan(folders.Get("SavedGames"), level2All: true);
        Scan(folders.Get("ProgramData"), level2All: false);

        // Drop folders nested in another candidate; stable order.
        var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var result = distinct.Where(p => !distinct.Any(o => !o.Equals(p, StringComparison.OrdinalIgnoreCase) &&
                                                             p.StartsWith(o.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        if (result.Count == 0) bundle.Notes.Add("No settings folder matching this game was found in Documents, AppData, LocalLow or Saved Games.");
        return result;
    }

    private sealed record CandidateFile(string Path, int Score);

    private IEnumerable<CandidateFile> FilesIn(string dir, EvidenceBundle bundle)
    {
        var result = new List<CandidateFile>();
        try
        {
            Walk(dir, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bundle.Notes.Add($"Could not fully read {Clean(_o.Folders.Tokenize(dir), _o.Folders)}: {Clean(ex.Message)}");
        }
        return result;

        void Walk(string d, int depth)
        {
            if (result.Count > 400) return;
            foreach (var f in SafeFiles(d))
            {
                var name = Path.GetFileName(f);
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (!TextExtensions.Contains(ext)) continue;
                var info = new FileInfo(f);
                if (info.Length == 0 || info.Length > _o.MaxFileBytes) continue;
                if (info.LinkTarget is not null) continue;
                var stem = Path.GetFileNameWithoutExtension(name);
                var settingsLike = SettingsLike.IsMatch(stem);
                if (ext == ".sav" && !settingsLike) continue;
                if (SaveLike.IsMatch(stem) && !settingsLike) continue;
                if (NoiseFile.IsMatch(stem) && !settingsLike) continue;
                if (ext == ".txt" && !settingsLike) continue;
                if (EvidencePolicy.CheckPath(_o.Folders.Tokenize(f), _o.Folders, out _) is not null)
                    continue; // e.g. a denied file name; discovery obeys the same rules as follow-ups
                var score = (settingsLike ? 50 : 0) + (ext is ".ini" or ".cfg" ? 30 : ext is ".json" or ".xml" or ".lsx" or ".yaml" or ".yml" ? 15 : 0)
                            + (info.Length < 64 * 1024 ? 10 : 0) - depth * 3;
                if (Regex.IsMatch(stem, @"(?i)gameusersettings|scalability|engine|settings|options|config")) score += 20;
                result.Add(new CandidateFile(f, score));
            }
            if (depth >= 6) return;
            foreach (var sub in SafeDirs(d))
            {
                if (SkipDirNames.Contains(Path.GetFileName(sub))) continue;
                Walk(sub, depth + 1);
            }
        }
    }

    /// <summary>Settings-looking files near the top of the install folder (some games keep config next to the exe).</summary>
    private IEnumerable<CandidateFile> InstallConfigFiles(string install)
    {
        var result = new List<CandidateFile>();
        try
        {
            void Take(string dir, int depth)
            {
                foreach (var f in SafeFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is not (".ini" or ".cfg" or ".json" or ".xml" or ".yaml" or ".yml" or ".conf")) continue;
                    var stem = Path.GetFileNameWithoutExtension(name);
                    if (NoiseFile.IsMatch(stem) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(SettingsLike.IsMatch(stem) || ext is ".ini" or ".cfg")) continue;
                    var info = new FileInfo(f);
                    if (info.Length == 0 || info.Length > 64 * 1024) continue;
                    result.Add(new CandidateFile(f, 5 - depth * 3));
                }
                if (depth >= 1) return;
                foreach (var sub in SafeDirs(dir))
                    if (Regex.IsMatch(Path.GetFileName(sub), @"(?i)^(cfg|config|configs|settings|data|bin)$")) Take(sub, depth + 1);
            }
            Take(install, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.Take(15);
    }

    // ------------------------------------------------------------ files

    private EvidenceSection? ReadFileSection(string path, LabFolders folders, EvidenceBundle bundle)
    {
        var token = Clean(folders.Tokenize(path), folders);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > _o.MaxFileBytes)
            {
                bundle.Notes.Add($"Skipped {token}: {info.Length} bytes is over the {_o.MaxFileBytes / (1024 * 1024)} MB limit.");
                return null;
            }
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var limit = (int)Math.Min(info.Length, _o.MaxFileBytes);
                bytes = new byte[limit];
                var read = 0;
                while (read < limit) { var n = fs.Read(bytes, read, limit - read); if (n <= 0) break; read += n; }
                if (read < limit) Array.Resize(ref bytes, read);
            }

            string body;
            if (TryDecodeText(bytes, out var text))
            {
                if (text.Length > _o.MaxTextBytes)
                    text = text[.._o.MaxTextBytes] + $"\n...(truncated: file is {bytes.Length} bytes, first {_o.MaxTextBytes} characters shown)";
                body = text;
            }
            else
            {
                body = $"(binary: {bytes.Length} bytes)";
                var strings = ExtractStrings(bytes, _o.MaxBinaryStringChars);
                if (strings.Length > 0) body += "\n" + strings;
            }
            return new EvidenceSection(EvidenceKind.File, token, Clean(body, folders));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bundle.Notes.Add($"Could not read {token}: {Clean(ex.Message, folders)}");
            return null;
        }
    }

    /// <summary>True (and the decoded text) for UTF-8 / UTF-16 text; false for binary data.</summary>
    internal static bool TryDecodeText(byte[] b, out string text)
    {
        text = "";
        if (b.Length == 0) return true;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { text = Encoding.UTF8.GetString(b, 3, b.Length - 3); return true; }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { text = Encoding.Unicode.GetString(b, 2, b.Length - 2); return true; }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { text = Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2); return true; }
        var sample = Math.Min(b.Length, 8192);
        var nul = 0;
        var oddNul = 0;
        for (var i = 0; i < sample; i++)
            if (b[i] == 0) { nul++; if ((i & 1) == 1) oddNul++; }
        if (nul == 0) { text = Encoding.UTF8.GetString(b); return !HasManyControlChars(text); }
        if (sample >= 4 && oddNul * 10 >= sample * 4) { text = Encoding.Unicode.GetString(b); return !HasManyControlChars(text); } // UTF-16LE without BOM
        return false;
    }

    private static bool HasManyControlChars(string s)
    {
        var sample = s.Length > 8192 ? s[..8192] : s;
        var bad = sample.Count(c => c < 32 && c is not ('\r' or '\n' or '\t') || c == '�');
        return bad * 20 > sample.Length; // > 5 %
    }

    /// <summary>Printable ASCII and UTF-16LE runs (≥ 5 chars) of a binary file, one per line, at most <paramref name="maxChars"/> characters.</summary>
    internal static string ExtractStrings(byte[] data, int maxChars)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var limit = maxChars * 2 / 3; // ASCII runs get two thirds, so UTF-16 text is never starved
        void Emit(string run)
        {
            if (run.Length < 5 || sb.Length >= limit || !seen.Add(run)) return;
            if (!run.Any(char.IsLetter)) return; // skip pure number/symbol noise
            var remaining = limit - sb.Length;
            var piece = run.Length + 1 > remaining ? run[..Math.Max(0, remaining - 1)] : run;
            sb.Append(piece).Append('\n');
        }

        var cur = new StringBuilder();
        foreach (var by in data)
        {
            if (by is >= 0x20 and <= 0x7E) cur.Append((char)by);
            else { Emit(cur.ToString()); cur.Clear(); }
            if (sb.Length >= limit) break;
        }
        Emit(cur.ToString());

        limit = maxChars;
        foreach (var start in new[] { 0, 1 }) // both alignments of UTF-16LE
        {
            cur.Clear();
            for (var i = start; i + 1 < data.Length && sb.Length < limit; i += 2)
            {
                if (data[i + 1] == 0 && data[i] is >= 0x20 and <= 0x7E) cur.Append((char)data[i]);
                else { Emit(cur.ToString()); cur.Clear(); }
            }
            Emit(cur.ToString());
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ------------------------------------------------------------ listings

    private void AddListing(EvidenceBundle bundle, LabFolders folders, string dir)
    {
        try
        {
            var lines = new List<string>();
            var total = 0;
            foreach (var d1 in SafeDirs(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                total++;
                if (lines.Count < _o.MaxListingEntries) lines.Add(Path.GetFileName(d1) + "/");
                foreach (var e in Entries(d1))
                {
                    total++;
                    if (lines.Count < _o.MaxListingEntries) lines.Add(Path.GetFileName(d1) + "/" + e);
                }
            }
            foreach (var f in SafeFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                total++;
                if (lines.Count < _o.MaxListingEntries) lines.Add(FileLine(f));
            }
            if (total > lines.Count) lines.Add($"... {total - lines.Count} more entries");
            if (lines.Count == 0) lines.Add("(empty)");
            bundle.Sections.Add(new EvidenceSection(EvidenceKind.Directory, Clean(folders.Tokenize(dir), folders), Clean(string.Join("\n", lines), folders)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bundle.Notes.Add($"Could not list {Clean(folders.Tokenize(dir), folders)}: {Clean(ex.Message, folders)}");
        }

        IEnumerable<string> Entries(string d)
        {
            foreach (var sub in SafeDirs(d).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) yield return Path.GetFileName(sub) + "/";
            foreach (var f in SafeFiles(d).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) yield return FileLine(f, nameOnly: true);
        }
    }

    private static string FileLine(string f, bool nameOnly = false)
    {
        long len;
        try { len = new FileInfo(f).Length; } catch (IOException) { len = -1; }
        return $"{Path.GetFileName(f)}  ({len} B)";
    }

    // ------------------------------------------------------------ registry

    private void CollectRegistry(HashSet<string> gameKeys, HashSet<string> pubKeys, EvidenceBundle bundle, LabFolders folders)
    {
        try
        {
            const string software = @"HKCU\Software";
            var keys = new List<string>();
            foreach (var name in _o.Registry.SubKeyNames(software))
            {
                if (name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                var key = software + "\\" + name;
                if (Matches(name, gameKeys)) keys.Add(key);
                else if (pubKeys.Count > 0 && Matches(name, pubKeys))
                    foreach (var sub in _o.Registry.SubKeyNames(key))
                        if (Matches(sub, gameKeys)) keys.Add(key + "\\" + sub);
            }
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
                AddRegistryKey(bundle, key, depth: 1, folders);
        }
        catch (Exception ex)
        {
            bundle.Notes.Add($"Registry lookup failed: {Clean(ex.Message, folders)}");
        }
    }

    private void AddRegistryKey(EvidenceBundle bundle, string key, int depth, LabFolders folders)
    {
        var values = _o.Registry.Values(key);
        if (values is { Count: > 0 })
        {
            var lines = values.Take(200).Select(v => $"{v.Name} = {v.Value.Replace("\r", "").Replace("\n", "\\n")}").ToList();
            if (values.Count > 200) lines.Add($"... {values.Count - 200} more values");
            bundle.Sections.Add(new EvidenceSection(EvidenceKind.Registry, Clean(key, folders), Clean(string.Join("\n", lines), folders)));
        }
        if (depth <= 0) return;
        foreach (var sub in _o.Registry.SubKeyNames(key).Take(10))
            AddRegistryKey(bundle, key + "\\" + sub, depth - 1, folders);
    }

    // ------------------------------------------------------------ helpers

    private string Clean(string text, LabFolders? folders = null) =>
        EvidenceSanitizer.Sanitize(text, folders ?? _o.Folders, _o.UserProfile, _o.UserName, _o.MachineName);

    private static void TryAdd(EvidenceBundle bundle, string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Warn($"game lab: {what} evidence failed: {ex.Message}");
            bundle.Notes.Add($"Could not collect the {what}: {ex.Message}");
        }
    }

    private static string? SafeDir(string? dir)
    {
        try { return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Sub folders, skipping junctions and symlinks (they could lead out of the allowed roots).</summary>
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

    /// <summary>Files matching an absolute pattern with *, ? and ** segments (no walking through reparse points).</summary>
    internal static List<string> ExpandGlob(string pattern, int limit)
    {
        var result = new List<string>();
        var norm = pattern.Replace('/', '\\');
        if (!norm.Contains('*') && !norm.Contains('?'))
        {
            if (File.Exists(norm)) result.Add(norm);
            return result;
        }
        var segs = norm.Split('\\');
        // First segment is the drive ("C:"), keep it as the start.
        Recurse(segs[0] + "\\", 1);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

        void Recurse(string dir, int i)
        {
            if (result.Count >= limit * 4) return;
            if (i >= segs.Length) return;
            var seg = segs[i];
            if (seg.Length == 0) { Recurse(dir, i + 1); return; }
            var last = i == segs.Length - 1;
            if (seg == "**")
            {
                // zero or more folders
                Recurse(dir, i + 1);
                foreach (var sub in SafeDirs(dir)) if (Depth(sub) < 12) Recurse(sub, i);
                return;
            }
            var wild = seg.Contains('*') || seg.Contains('?');
            if (last)
            {
                if (!wild) { var f = Path.Combine(dir, seg); if (File.Exists(f)) result.Add(f); }
                else
                    foreach (var f in SafeFiles(dir))
                        if (WildMatch(seg, Path.GetFileName(f))) result.Add(f);
                return;
            }
            if (!wild) { var next = Path.Combine(dir, seg); if (Directory.Exists(next) && (File.GetAttributes(next) & FileAttributes.ReparsePoint) == 0) Recurse(next, i + 1); return; }
            foreach (var sub in SafeDirs(dir))
                if (WildMatch(seg, Path.GetFileName(sub))) Recurse(sub, i + 1);
        }

        static int Depth(string p) => p.Count(c => c == '\\');
    }

    internal static bool WildMatch(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
}
