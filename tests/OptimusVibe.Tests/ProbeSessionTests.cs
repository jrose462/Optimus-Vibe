using OptimusVibe.GameLab;
using OptimusVibe.Launch;

namespace OptimusVibe.Tests;

public class ProbeSessionTests
{
    private const string Ini = "[Display]\nResolutionSizeX=1920\nQuality=3\nPlayTime=10\n";

    private static (LabProfile P, ProbeSession S) Make(ProbeLimits? limits = null)
    {
        var p = new LabProfile();
        return (p, new ProbeSession(p.Target(), p.Options(), limits));
    }

    [Fact]
    public void Diff_gives_key_level_changes_for_ini_and_hash_only_for_binary()
    {
        var (p, s) = Make();
        using var _ = p;
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", Ini);
        p.WriteBytes(p.Documents, @"Borderlands4\profile.sav", [0, 1, 2, 3, 0, 0, 9]);
        var before = s.Snapshot();

        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nResolutionSizeX=1280\nQuality=3\nVSync=1\n");
        p.WriteBytes(p.Documents, @"Borderlands4\profile.sav", [0, 1, 2, 3, 0, 0, 8]);
        var diff = ProbeSession.Diff(before, s.Snapshot());

        var ini = diff.Files.Single(f => f.Path.EndsWith("GameUserSettings.ini"));
        Assert.True(ini.IsText);
        Assert.Equal("ini", ini.Format);
        Assert.Contains(new KeyChange("[Display]ResolutionSizeX", "1920", "1280"), ini.Keys);
        Assert.Contains(new KeyChange("[Display]VSync", null, "1"), ini.Keys);
        Assert.Contains(new KeyChange("[Display]PlayTime", "10", null), ini.Keys);
        Assert.DoesNotContain(ini.Keys, k => k.Key == "[Display]Quality");

        var sav = diff.Files.Single(f => f.Path.EndsWith("profile.sav"));
        Assert.True(sav.IsBinary);
        Assert.Empty(sav.Keys);
        Assert.NotEqual(sav.HashBefore, sav.HashAfter);
        Assert.StartsWith("{Documents}", sav.Path);
    }

    [Fact]
    public void Diff_reports_added_and_removed_files_and_json_and_unknown_text()
    {
        var (p, s) = Make();
        using var _ = p;
        p.Write(p.Documents, @"Borderlands4\old.json", "{\"a\":1}");
        p.Write(p.Documents, @"Borderlands4\notes.txt", "hello\nworld\n");
        var before = s.Snapshot();
        File.Delete(Path.Combine(p.Documents, "Borderlands4", "old.json"));
        p.Write(p.Documents, @"Borderlands4\new.json", "{\"gfx\":{\"dlss\":true},\"list\":[1,2]}");
        p.Write(p.Documents, @"Borderlands4\notes.txt", "hello\nthere\n");
        var diff = ProbeSession.Diff(before, s.Snapshot());

        Assert.Equal(ProbeChangeKind.Removed, diff.Files.Single(f => f.Path.EndsWith("old.json")).Kind);
        var added = diff.Files.Single(f => f.Path.EndsWith("new.json"));
        Assert.Equal(ProbeChangeKind.Added, added.Kind);
        Assert.Contains(new KeyChange("gfx.dlss", null, "true"), added.Keys);
        Assert.Contains(new KeyChange("list[1]", null, "2"), added.Keys);
        var notes = diff.Files.Single(f => f.Path.EndsWith("notes.txt"));
        Assert.Equal("lines", notes.Format);
        Assert.Contains(new KeyChange("(line)", null, "there"), notes.Keys);
        Assert.Contains(new KeyChange("(line)", "world", null), notes.Keys);
    }

    [Fact]
    public void Diff_registry_values_old_to_new()
    {
        var (p, s) = Make();
        using var _ = p;
        p.Registry.Add(@"HKCU\Software\Gearbox\Borderlands4", ("Quality", "2"), ("Same", "x"));
        var before = s.Snapshot();
        p.Registry.Add(@"HKCU\Software\Gearbox\Borderlands4", ("Quality", "0"), ("Same", "x"), ("New", "1"));
        var diff = ProbeSession.Diff(before, s.Snapshot());
        Assert.Contains(new RegistryChange(@"HKCU\Software\Gearbox\Borderlands4", "Quality", "2", "0"), diff.Registry);
        Assert.Contains(new RegistryChange(@"HKCU\Software\Gearbox\Borderlands4", "New", null, "1"), diff.Registry);
        Assert.Equal(2, diff.Registry.Count);
    }

    [Fact]
    public void Snapshot_respects_the_file_bound_and_skips_noise_folders()
    {
        var (p, s) = Make(new ProbeLimits { MaxFiles = 3 });
        using var _ = p;
        for (var i = 0; i < 6; i++) p.Write(p.Documents, $@"Borderlands4\f{i}.ini", "[a]\nb=1\n");
        var snap = s.Snapshot();
        Assert.Equal(3, snap.Files.Count);
        Assert.Contains(snap.Notes, n => n.Contains("not fingerprinted"));

        var (p2, s2) = Make();
        using var __ = p2;
        p2.Write(p2.Documents, @"Borderlands4\Logs\x.ini", "[a]\nb=1\n");
        p2.Write(p2.Documents, @"Borderlands4\keep.ini", "[a]\nb=1\n");
        Assert.Single(s2.Snapshot().Files);
    }

    [Fact]
    public void Snapshot_hash_budget_falls_back_to_size_and_time()
    {
        var (p, s) = Make(new ProbeLimits { MaxHashBytes = 10 });
        using var _ = p;
        p.Write(p.Documents, @"Borderlands4\big.ini", "[a]\nb=1\nc=2\nd=3\n");
        var snap = s.Snapshot();
        Assert.Null(snap.Files.Values.Single().Sha256);
        Assert.Contains(snap.Notes, n => n.Contains("size and time"));
    }

    [Fact]
    public void ToEvidence_splits_pass_a_noise_from_pass_b_settings_and_sanitizes()
    {
        var (p, s) = Make();
        using var _ = p;
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=3\nLastPlayed=1\nAuthToken=abc123\n");
        p.WriteBytes(p.Documents, @"Borderlands4\state.sav", [0, 0, 1]);
        p.WriteBytes(p.Documents, @"Borderlands4\gfx.sav", [0, 0, 1]);
        var s0 = s.Snapshot();
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=3\nLastPlayed=2\nAuthToken=abc123\n");
        p.WriteBytes(p.Documents, @"Borderlands4\state.sav", [0, 0, 2]);
        var s1 = s.Snapshot();
        p.Write(p.Documents, @"Borderlands4\GameUserSettings.ini", "[Display]\nQuality=0\nLastPlayed=3\nAuthToken=zzz999\n");
        p.WriteBytes(p.Documents, @"Borderlands4\state.sav", [0, 0, 3]);
        p.WriteBytes(p.Documents, @"Borderlands4\gfx.sav", [0, 0, 7]);
        var s2 = s.Snapshot();

        var ev = s.ToEvidence(ProbeSession.Diff(s0, s1), ProbeSession.Diff(s1, s2));
        var text = ev.Render().ReplaceLineEndings("\n");

        Assert.Contains("## Pass A: rewritten every session", text);
        Assert.Contains("## Pass B: settings changed (beyond pass A)", text);
        var a = ev.PassA.ReplaceLineEndings("\n");
        var b = ev.PassB.ReplaceLineEndings("\n");
        Assert.Contains("[Display]LastPlayed: 1 -> 2", a);
        Assert.Contains("state.sav", a);
        Assert.Contains("[Display]Quality: 3 -> 0", b);
        Assert.DoesNotContain("[Display]LastPlayed: 2 -> 3", b);   // pass A changed it too
        Assert.Contains("gfx.sav", b);
        Assert.Equal([@"{Documents}\Borderlands4\gfx.sav"], ev.OpaqueFiles);
        Assert.DoesNotContain("abc123", text);                       // secret-looking keys never leave
        Assert.DoesNotContain("zzz999", text);
        Assert.DoesNotContain(p.Root, text);
        Assert.Contains(@"{Documents}\Borderlands4", text);
        Assert.True(ev.HasSettings);
    }

    [Fact]
    public void ToEvidence_without_pass_b_and_probe_section_heading()
    {
        var (p, s) = Make();
        using var _ = p;
        var ev = s.ToEvidence(new ProbeDiff([], [], []), null);
        Assert.Contains("pass B was not run", ev.PassB);
        Assert.Contains("(no files or registry values changed)", ev.PassA);
        Assert.Equal("### Mapping run", new EvidenceSection(EvidenceKind.Probe, "", ev.Render()).Heading);
    }

    [Fact]
    public void Collector_adds_the_mapping_run_section()
    {
        using var p = new LabProfile();
        var o = p.Options();
        var collector = new EvidenceCollector(new EvidenceOptions
        {
            Folders = o.Folders, Registry = o.Registry, UserProfile = o.UserProfile, UserName = o.UserName, MachineName = o.MachineName,
            Probe = _ => "pass A: nothing",
        });
        var bundle = collector.Collect(p.Target());
        Assert.Equal(1, bundle.Count(EvidenceKind.Probe));
        Assert.Contains("### Mapping run\n", bundle.Render().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ProbeStore_round_trips()
    {
        using var t = new TempDir();
        Assert.Null(ProbeStore.Get(t.Path, "steam:1"));
        ProbeStore.Record(t.Path, "steam:1", new ProbeRecord { Utc = DateTime.UtcNow, Evidence = "x", OpaqueFiles = ["a"] });
        var r = ProbeStore.Get(t.Path, "STEAM:1");
        Assert.Equal("x", r!.Evidence);
        Assert.Equal(["a"], r.OpaqueFiles);
    }

    // ------------------------------------------------------------ --probe flag and marker

    [Fact]
    public void RunArgs_parses_probe()
    {
        var r = RunArgs.Parse(["run", "forspoken", "--probe", "--", "game.exe"]);
        Assert.True(r.Probe);
        Assert.True(r.ToOptions().Probe);
        Assert.Equal("forspoken", r.Game);
        Assert.Equal(["game.exe"], r.Command);
        Assert.False(RunArgs.Parse(["run", "forspoken"]).Probe);
    }

    [Fact]
    public void Marker_expires_after_30_minutes_and_is_per_game()
    {
        using var t = new TempDir();
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(ProbeMarker.IsActive(t.Path, "g", now));
        ProbeMarker.Set(t.Path, "g", now);
        Assert.True(ProbeMarker.IsActive(t.Path, "g", now.AddMinutes(29)));
        Assert.True(ProbeMarker.IsActive(t.Path, "G", now.AddMinutes(1)));
        Assert.False(ProbeMarker.IsActive(t.Path, "other", now.AddMinutes(1)));
        Assert.False(ProbeMarker.IsActive(t.Path, "g", now.AddMinutes(31)));
        ProbeMarker.Clear(t.Path, "g");
        Assert.False(ProbeMarker.IsActive(t.Path, "g", now.AddMinutes(1)));
        Assert.False(File.Exists(ProbeMarker.FileFor(t.Path)));
    }

    [Fact]
    public void Marker_ignores_a_corrupt_file()
    {
        using var t = new TempDir();
        File.WriteAllText(ProbeMarker.FileFor(t.Path), "{ not json");
        Assert.False(ProbeMarker.IsActive(t.Path, "g"));
    }
}
