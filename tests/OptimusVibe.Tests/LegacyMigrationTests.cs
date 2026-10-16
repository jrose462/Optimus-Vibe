using System.Text.Json.Nodes;
using OptimusVibe.Config;
using OptimusVibe.Sync;

namespace OptimusVibe.Tests;

public sealed class LegacyMigrationTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string Old => _tmp.File("GameProfiles");
    private string New => _tmp.File("OptimusVibe");

    public void Dispose() => _tmp.Dispose();

    private void SeedOld()
    {
        _tmp.Write(@"GameProfiles\settings.json",
            $$"""{"configRoot":"{{(Old + @"\repos\jrose462\GameProfiles\config").Replace("\\", "\\\\")}}","syncProvider":"github","gitHubRepo":"jrose462/GameProfiles","autoSync":false}""");
        _tmp.Write(@"GameProfiles\selections.json", "{\"sel\":1}");
        _tmp.Write(@"GameProfiles\state.json", "{}");
        _tmp.Write(@"GameProfiles\update-check.json", "{\"u\":1}");
        _tmp.Write(@"GameProfiles\backups\eldenring\original\manifest.json", "[]");
        _tmp.Write(@"GameProfiles\repos\jrose462\GameProfiles\.gameprofiles-sync.json", """{"repo":"jrose462/GameProfiles","branch":"main"}""");
        _tmp.Write(@"GameProfiles\repos\jrose462\GameProfiles\config\devices.yaml", "devices: []");
        _tmp.Write(@"GameProfiles\logs\gameprofiles-20260101.log", "hello");
    }

    [Fact]
    public void Migrates_files_rewrites_old_names_and_keeps_old_folder()
    {
        SeedOld();
        var r = LegacyMigration.Run(Old, New, null, null);

        Assert.True(r.Ran);
        Assert.NotEmpty(r.Notes);
        Assert.True(File.Exists(Path.Combine(New, LegacyMigration.MarkerFileName)));
        Assert.Equal("{\"sel\":1}", File.ReadAllText(Path.Combine(New, "selections.json")));
        Assert.True(File.Exists(Path.Combine(New, "state.json")));
        Assert.True(File.Exists(Path.Combine(New, "update-check.json")));
        Assert.True(File.Exists(Path.Combine(New, "backups", "eldenring", "original", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(New, "logs", "optimusvibe-20260101.log")));

        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(New, "settings.json")))!;
        Assert.Equal("jrose462/Optimus-Vibe", (string?)s["gitHubRepo"]);
        Assert.Equal(false, (bool?)s["autoSync"]);
        Assert.Equal(Path.Combine(New, "repos", "jrose462", "Optimus-Vibe", "config"), (string?)s["configRoot"]);

        var mirror = Path.Combine(New, "repos", "jrose462", "Optimus-Vibe");
        Assert.True(File.Exists(Path.Combine(mirror, "config", "devices.yaml")));
        Assert.False(File.Exists(Path.Combine(mirror, LegacyMigration.OldStateFileName)));
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(mirror, GitHubMirror.StateFileName)))!;
        Assert.Equal("jrose462/Optimus-Vibe", (string?)state["repo"]);

        // the old folder is untouched
        Assert.True(File.Exists(Path.Combine(Old, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Old, "repos", "jrose462", "GameProfiles", LegacyMigration.OldStateFileName)));
    }

    [Fact]
    public void Custom_repo_is_left_alone()
    {
        _tmp.Write(@"GameProfiles\settings.json", """{"gitHubRepo":"someone/else"}""");
        LegacyMigration.Run(Old, New, null, null);
        var s = JsonNode.Parse(File.ReadAllText(Path.Combine(New, "settings.json")))!;
        Assert.Equal("someone/else", (string?)s["gitHubRepo"]);
    }

    [Fact]
    public void Does_not_repeat_or_overwrite()
    {
        SeedOld();
        Assert.True(LegacyMigration.Run(Old, New, null, null).Ran);
        File.WriteAllText(Path.Combine(New, "selections.json"), "{\"changed\":true}");
        _tmp.Write(@"GameProfiles\extra.json", "x");
        Assert.False(LegacyMigration.Run(Old, New, null, null).Ran);
        Assert.Equal("{\"changed\":true}", File.ReadAllText(Path.Combine(New, "selections.json")));
    }

    [Fact]
    public void Skips_when_new_folder_already_has_settings_or_old_is_missing()
    {
        SeedOld();
        _tmp.Write(@"OptimusVibe\settings.json", "{}");
        Assert.False(LegacyMigration.Run(Old, New, null, null).Ran);
        Assert.False(File.Exists(Path.Combine(New, "state.json")));

        using var other = new TempDir();
        Assert.False(LegacyMigration.Run(other.File("nope"), other.File("new"), null, null).Ran);
        Assert.False(Directory.Exists(other.File("new")));
    }

    [Fact]
    public void Copies_credential_to_new_target_and_keeps_old()
    {
        SeedOld();
        var oldT = "GameProfiles:test:" + Guid.NewGuid().ToString("N");
        var newT = "OptimusVibe:test:" + Guid.NewGuid().ToString("N");
        try
        {
            GitHubAuth.SaveToken("ghp_old_token", oldT);
            Assert.True(LegacyMigration.Run(Old, New, oldT, newT).Ran);
            Assert.Equal("ghp_old_token", GitHubAuth.LoadToken(newT));
            Assert.Equal("ghp_old_token", GitHubAuth.LoadToken(oldT));
        }
        finally
        {
            GitHubAuth.ClearToken(oldT);
            GitHubAuth.ClearToken(newT);
        }
    }

    [Fact]
    public void Existing_new_credential_is_not_replaced()
    {
        SeedOld();
        var oldT = "GameProfiles:test:" + Guid.NewGuid().ToString("N");
        var newT = "OptimusVibe:test:" + Guid.NewGuid().ToString("N");
        try
        {
            GitHubAuth.SaveToken("ghp_old_token", oldT);
            GitHubAuth.SaveToken("ghp_new_token", newT);
            LegacyMigration.Run(Old, New, oldT, newT);
            Assert.Equal("ghp_new_token", GitHubAuth.LoadToken(newT));
        }
        finally
        {
            GitHubAuth.ClearToken(oldT);
            GitHubAuth.ClearToken(newT);
        }
    }

    [Fact]
    public void Env_prefers_new_name_and_falls_back_to_legacy()
    {
        const string v = "TESTONLY_X";
        try
        {
            Assert.Null(AppPaths.Env(v));
            Environment.SetEnvironmentVariable("GAMEPROFILES_" + v, "legacy");
            Assert.Equal("legacy", AppPaths.Env(v));
            Environment.SetEnvironmentVariable("OPTIMUSVIBE_" + v, "new");
            Assert.Equal("new", AppPaths.Env(v));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMEPROFILES_" + v, null);
            Environment.SetEnvironmentVariable("OPTIMUSVIBE_" + v, null);
        }
    }
}
