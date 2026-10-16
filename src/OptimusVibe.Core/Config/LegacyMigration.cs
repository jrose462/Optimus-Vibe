using System.Text.Json;
using System.Text.Json.Nodes;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.Config;

/// <summary>What <see cref="LegacyMigration.Run(string, string, string?, string?)"/> did.</summary>
public sealed record MigrationResult(bool Ran, IReadOnlyList<string> Notes);

/// <summary>
/// One-time move from the product's former name (GameProfiles) to Optimus-Vibe: copies the per-machine data folder
/// (%LOCALAPPDATA%\GameProfiles → %LOCALAPPDATA%\OptimusVibe), rewrites values that embed the old names and copies the
/// GitHub token from the old Credential Manager target to the new one. The old folder and credential are left in
/// place. Called once at startup by the app and the CLI, before anything reads settings.
/// </summary>
public static class LegacyMigration
{
    public const string MarkerFileName = ".migrated-from-gameprofiles";
    public const string OldRepo = "jrose462/GameProfiles";
    public const string NewRepo = "jrose462/Optimus-Vibe";
    public const string OldStateFileName = ".gameprofiles-sync.json";

    private static readonly string[] Files = ["settings.json", "selections.json", "state.json", "update-check.json"];
    private static readonly string[] Dirs = ["backups", "repos", "logs"];

    /// <summary>Startup entry point: default folders, default credential targets. Never throws.</summary>
    public static MigrationResult Run()
    {
        try
        {
            // An explicit OPTIMUSVIBE_DATA means a custom/test data folder: only the matching GAMEPROFILES_DATA (if any)
            // is a legacy source — never the real %LOCALAPPDATA%\GameProfiles.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var newEnv = Environment.GetEnvironmentVariable("OPTIMUSVIBE_DATA");
            var oldEnv = Environment.GetEnvironmentVariable("GAMEPROFILES_DATA");
            if (!string.IsNullOrEmpty(newEnv))
            {
                if (string.IsNullOrEmpty(oldEnv)) return new MigrationResult(false, []);
                return Run(oldEnv, newEnv, null, null);
            }
            if (!string.IsNullOrEmpty(oldEnv)) return new MigrationResult(false, []); // legacy variable still names the live folder
            return Run(Path.Combine(local, "GameProfiles"), Path.Combine(local, "OptimusVibe"),
                GitHubAuth.LegacyCredentialTarget, GitHubAuth.CredentialTarget);
        }
        catch (Exception ex)
        {
            Log.Warn($"migration from GameProfiles failed: {ex.Message}");
            return new MigrationResult(false, [ex.Message]);
        }
    }

    /// <summary>
    /// Migrates <paramref name="oldDir"/> into <paramref name="newDir"/> unless the new folder already has settings or a
    /// marker. Credential targets may be null to skip the token copy.
    /// </summary>
    public static MigrationResult Run(string oldDir, string newDir, string? oldCredentialTarget, string? newCredentialTarget)
    {
        var notes = new List<string>();
        void Note(string m) { notes.Add(m); Log.Info("migration: " + m); }

        oldDir = Path.GetFullPath(oldDir);
        newDir = Path.GetFullPath(newDir);
        if (oldDir.TrimEnd('\\').Equals(newDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return new MigrationResult(false, notes);
        if (File.Exists(Path.Combine(newDir, MarkerFileName))) return new MigrationResult(false, notes);
        if (!Directory.Exists(oldDir)) return new MigrationResult(false, notes);
        if (File.Exists(Path.Combine(newDir, "settings.json"))) return new MigrationResult(false, notes);

        Note($"copying {oldDir} to {newDir} (the old folder is left in place)");
        Directory.CreateDirectory(newDir);

        foreach (var name in Files)
        {
            var src = Path.Combine(oldDir, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(newDir, name);
            try
            {
                if (name == "settings.json") File.WriteAllText(dst, RewriteSettings(File.ReadAllText(src), oldDir, newDir, Note));
                else File.Copy(src, dst, overwrite: true);
                Note($"copied {name}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Note($"could not copy {name}: {ex.Message}");
            }
        }

        foreach (var name in Dirs)
        {
            var src = Path.Combine(oldDir, name);
            if (!Directory.Exists(src)) continue;
            try
            {
                var n = CopyDir(src, Path.Combine(newDir, name), name == "repos");
                Note($"copied {name}\\ ({n} file(s))");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Note($"could not copy {name}\\: {ex.Message}");
            }
        }

        if (oldCredentialTarget is not null && newCredentialTarget is not null)
        {
            try
            {
                if (GitHubAuth.LoadToken(newCredentialTarget) is null && GitHubAuth.LoadToken(oldCredentialTarget) is { } token)
                {
                    GitHubAuth.SaveToken(token, newCredentialTarget);
                    Note($"copied the GitHub token from '{oldCredentialTarget}' to '{newCredentialTarget}' (the old one is kept)");
                }
            }
            catch (Exception ex)
            {
                Note($"could not copy the GitHub token: {ex.Message}");
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(newDir, MarkerFileName),
                $"Migrated from {oldDir} on {DateTime.UtcNow:O}. Delete the old folder whenever you like.{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note($"could not write the marker file: {ex.Message}");
        }
        return new MigrationResult(true, notes);
    }

    /// <summary>Rewrites old default repo / old data folder paths in settings.json text (other values untouched).</summary>
    internal static string RewriteSettings(string json, string oldDir, string newDir, Action<string> note)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return json;
        foreach (var key in root.Select(kv => kv.Key).ToList())
        {
            if (root[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
            var t = s;
            if (t.Equals(OldRepo, StringComparison.OrdinalIgnoreCase)) t = NewRepo;
            if (t.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase)) t = newDir + t[oldDir.Length..];
            // mirror path: ...\repos\<owner>\GameProfiles → ...\repos\<owner>\Optimus-Vibe (only for the old default repo)
            t = t.Replace(@"\repos\jrose462\GameProfiles", @"\repos\jrose462\Optimus-Vibe", StringComparison.OrdinalIgnoreCase);
            if (t != s)
            {
                root[key] = t;
                note($"settings.json {key}: {s} -> {t}");
            }
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static int CopyDir(string src, string dst, bool isRepos)
    {
        var count = 0;
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("gameprofiles-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                name = "optimusvibe-" + name["gameprofiles-".Length..];
            var target = Path.Combine(dst, name);
            if (name.Equals(OldStateFileName, StringComparison.OrdinalIgnoreCase))
            {
                target = Path.Combine(dst, GitHubMirror.StateFileName);
                File.WriteAllText(target, RewriteStateRepo(File.ReadAllText(file)));
            }
            else File.Copy(file, target, overwrite: true);
            count++;
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            // repos\<owner>\GameProfiles is the mirror of the old default repo: it moves to repos\<owner>\Optimus-Vibe.
            if (isRepos && name.Equals("GameProfiles", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(Path.GetDirectoryName(dir))!.Equals("jrose462", StringComparison.OrdinalIgnoreCase))
                name = "Optimus-Vibe";
            count += CopyDir(dir, Path.Combine(dst, name), isRepos);
        }
        return count;
    }

    private static string RewriteStateRepo(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject o && o["repo"]?.GetValue<string>() is { } repo && repo.Equals(OldRepo, StringComparison.OrdinalIgnoreCase))
            {
                o["repo"] = NewRepo;
                return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return json;
    }
}
