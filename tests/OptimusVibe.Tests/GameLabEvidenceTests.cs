using System.Text;
using OptimusVibe.GameLab;

namespace OptimusVibe.Tests;

public class GameLabEvidenceTests
{
    private const string Gus = "[Display]\nResolutionSizeX=2560\nResolutionSizeY=1440\nFullscreenMode=1\n";

    // ------------------------------------------------------------ tokenization + sanitization

    [Fact]
    public void Tokenize_picks_the_longest_known_folder()
    {
        using var p = new LabProfile();
        var f = p.Folders;
        Assert.Equal(@"{LocalAppData}\OakGame\a.ini", f.Tokenize(Path.Combine(p.LocalAppData, "OakGame", "a.ini")));
        Assert.Equal(@"{LocalLow}\Pub\Game", f.Tokenize(Path.Combine(p.LocalLow, "Pub", "Game")));
        Assert.Equal(@"{Documents}\My Games", f.Tokenize(Path.Combine(p.Documents, "My Games")));
        Assert.Equal(@"{UserProfile}\Desktop", f.Tokenize(Path.Combine(p.Root, "Desktop")));
        Assert.Equal(@"Z:\elsewhere\x", f.Tokenize(@"Z:\elsewhere\x"));
        // a sibling that only shares a prefix is not "under" the folder
        Assert.Equal(p.Root + "2\\x", f.Tokenize(p.Root + "2\\x"));
    }

    [Fact]
    public void TokenizeText_handles_single_double_and_forward_slashes()
    {
        using var p = new LabProfile();
        var local = p.LocalAppData;
        var text = $"a={local}\\Foo\\b.ini\n\"json\": \"{local.Replace("\\", "\\\\")}\\\\Foo\"\nu={local.Replace('\\', '/')}/Foo";
        var t = p.Folders.TokenizeText(text);
        Assert.DoesNotContain(local, t);
        Assert.Equal(3, t.Split("{LocalAppData}").Length - 1);
    }

    [Fact]
    public void Sanitize_redacts_user_machine_email_steam_ids_and_secret_values()
    {
        using var p = new LabProfile("alice");
        var text = $"owner={p.User}\nhost=TESTPC\nmail=alice@example.com\nlast={p.Documents}\\x\nsteam=76561198012345678\n" +
                   "AuthToken=abcDEF123456\nPassword = hunter2hunter2\n{\"accessToken\": \"zzz\", \"Quality\": 3}\nuserdata\\123456789\\remote\nGraphicsQuality=3";
        var s = EvidenceSanitizer.Sanitize(text, p.Folders, p.Root, p.User, "TESTPC");
        Assert.DoesNotContain("alice", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TESTPC", s);
        Assert.DoesNotContain("example.com", s);
        Assert.DoesNotContain("76561198012345678", s);
        Assert.DoesNotContain("hunter2", s);
        Assert.DoesNotContain("abcDEF", s);
        Assert.DoesNotContain("zzz", s);
        Assert.DoesNotContain("123456789", s);
        Assert.Contains("{Documents}\\x", s);                 // tokenized paths stay readable
        Assert.Contains("GraphicsQuality=3", s);              // settings survive
        Assert.Contains("\"Quality\": 3", s);
    }

    // ------------------------------------------------------------ discovery

    private static string Section(EvidenceBundle b, EvidenceKind k, string titleContains) =>
        b.Sections.Single(s => s.Kind == k && s.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)).Body;

    [Fact]
    public void Collect_finds_settings_by_game_name_install_folder_ue_project_and_publisher()
    {
        using var p = new LabProfile();
        // install folder with an Unreal project
        Directory.CreateDirectory(Path.Combine(p.Install, "OakGame", "Binaries", "Win64"));
        p.Write(p.Install, "Borderlands4.exe", "MZ");
        // by game name (Documents\My Games)
        p.Write(p.Documents, @"My Games\Borderlands 4\Saved\Config\Windows\GameUserSettings.ini", Gus);
        // by UE project name (LocalAppData\OakGame)
        p.Write(p.LocalAppData, @"OakGame\Saved\Config\WindowsNoEditor\Engine.ini", "[SystemSettings]\nr.VSync=0\n");
        // by publisher\game (LocalLow\Gearbox\Borderlands4)
        p.Write(p.LocalLow, @"Gearbox\Borderlands 4\settings.json", "{\"quality\": 2}");
        // SavedGames two levels
        p.Write(p.SavedGames, @"Gearbox\Borderlands 4\Controls.cfg", "bind w forward\n");
        // noise that must not be collected
        p.Write(p.LocalAppData, @"Contoso\Other\settings.ini", "x=1\n");
        p.Write(p.Documents, @"My Games\Borderlands 4\Saved\SaveGames\profile1.sav", "SAVEDATA");
        p.Write(p.Documents, @"My Games\Borderlands 4\Saved\Logs\game.ini", "log=1\n");
        p.WriteBytes(p.LocalLow, @"Gearbox\Borderlands 4\huge.json", new byte[3 * 1024 * 1024]);
        p.Registry.Add(@"HKCU\Software\Gearbox\Borderlands 4", ("Quality", "3"), ("Hdr", "1"));
        p.Registry.Add(@"HKCU\Software\Contoso\Other", ("x", "1"));

        var bundle = new EvidenceCollector(p.Options(cloud: _ => "Verdict: Settings sync between your devices", config: _ => "id: borderlands-4\n")).Collect(p.Target());

        var titles = bundle.Sections.Where(s => s.Kind == EvidenceKind.File).Select(s => s.Title).ToList();
        Assert.Contains(@"{Documents}\My Games\Borderlands 4\Saved\Config\Windows\GameUserSettings.ini", titles);
        Assert.Contains(@"{LocalAppData}\OakGame\Saved\Config\WindowsNoEditor\Engine.ini", titles);
        Assert.Contains(@"{LocalLow}\Gearbox\Borderlands 4\settings.json", titles);
        Assert.Contains(@"{SavedGames}\Gearbox\Borderlands 4\Controls.cfg", titles);
        Assert.DoesNotContain(titles, t => t.Contains("Contoso") || t.Contains("SaveGames") || t.Contains("Logs") || t.Contains("huge.json"));

        Assert.Contains("ResolutionSizeX=2560", Section(bundle, EvidenceKind.File, "GameUserSettings.ini"));
        Assert.Contains("Quality = 3", Section(bundle, EvidenceKind.Registry, @"Gearbox\Borderlands 4"));
        Assert.DoesNotContain(bundle.Sections, s => s.Kind == EvidenceKind.Registry && s.Title.Contains("Contoso"));
        Assert.Contains(bundle.Sections, s => s.Kind == EvidenceKind.Directory && s.Title == @"{Documents}\My Games\Borderlands 4");
        Assert.Contains(bundle.Sections, s => s.Kind == EvidenceKind.Directory && s.Title.StartsWith("{LocalAppData}\\OakGame"));
        var install = bundle.Sections.Single(s => s.Kind == EvidenceKind.Install);
        Assert.Contains("Unreal Engine", install.Body);
        Assert.Contains("OakGame", install.Body);
        Assert.Contains("Borderlands4.exe", install.Body);
        Assert.Contains("sync between", Section(bundle, EvidenceKind.CloudCheck, ""));
        Assert.Contains("borderlands-4", Section(bundle, EvidenceKind.CurrentConfig, ""));
    }

    [Fact]
    public void Collect_is_deterministic_and_renders_the_documented_headings()
    {
        using var p = new LabProfile();
        p.Write(p.Documents, @"My Games\Borderlands 4\a.ini", "a=1\n");
        p.Write(p.Documents, @"My Games\Borderlands 4\b.ini", "b=2\n");
        var t = p.Target();
        var one = new EvidenceCollector(p.Options()).Collect(t).Render();
        var two = new EvidenceCollector(p.Options()).Collect(t).Render();
        Assert.Equal(one, two);
        Assert.Contains("### File: {Documents}\\My Games\\Borderlands 4\\a.ini\n```\na=1\n```", one);
        Assert.Contains("### Directory: {Documents}\\My Games\\Borderlands 4\n", one);
        Assert.Contains("### Install: ", one);
    }

    [Fact]
    public void Saves_are_skipped_unless_they_look_like_settings()
    {
        using var p = new LabProfile();
        p.Write(p.Documents, @"My Games\Borderlands 4\Slot1.sav", "SAVE");
        p.Write(p.Documents, @"My Games\Borderlands 4\GameSettings.sav", "SETTINGSAV");
        var titles = new EvidenceCollector(p.Options()).Collect(p.Target()).Sections.Where(s => s.Kind == EvidenceKind.File).Select(s => s.Title).ToList();
        Assert.Contains(titles, t => t.EndsWith("GameSettings.sav"));
        Assert.DoesNotContain(titles, t => t.EndsWith("Slot1.sav"));
    }

    [Fact]
    public void Binary_files_give_a_byte_count_and_up_to_4kb_of_strings()
    {
        using var p = new LabProfile();
        var bytes = new List<byte> { 0, 1, 2, 0 };
        bytes.AddRange(Encoding.ASCII.GetBytes("GraphicsQuality"));
        bytes.AddRange([0, 0, 0xFF]);
        bytes.AddRange(Encoding.Unicode.GetBytes("ShadowDetail"));
        bytes.AddRange([0, 0, 0xFF]);
        for (var i = 0; i < 600; i++) { bytes.AddRange(Encoding.ASCII.GetBytes($"some_long_setting_name_{i}")); bytes.Add(0); }
        p.WriteBytes(p.Documents, @"My Games\Borderlands 4\Prefs.sav", bytes.ToArray()); // "Prefs" is settings-like
        var body = Section(new EvidenceCollector(p.Options()).Collect(p.Target()), EvidenceKind.File, "Prefs.sav");
        Assert.StartsWith($"(binary: {bytes.Count} bytes)", body);
        Assert.Contains("GraphicsQuality", body);
        Assert.Contains("ShadowDetail", body);
        Assert.True(body.Length < 4096 + 100, $"strings must be capped, got {body.Length}");
    }

    [Fact]
    public void Utf16_text_files_are_decoded()
    {
        using var p = new LabProfile();
        var path = Path.Combine(p.Documents, "My Games", "Borderlands 4", "Options.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[Video]\nVSync=1\n")).ToArray());
        Assert.Contains("VSync=1", Section(new EvidenceCollector(p.Options()).Collect(p.Target()), EvidenceKind.File, "Options.ini"));
    }

    [Fact]
    public void Text_over_200kb_is_truncated_and_never_throws_on_missing_install()
    {
        using var p = new LabProfile();
        p.Write(p.Documents, @"My Games\Borderlands 4\Settings.json", new string('x', 500_000));
        var t = p.Target() with { InstallDir = Path.Combine(p.Tmp.Path, "gone") };
        var bundle = new EvidenceCollector(p.Options()).Collect(t);
        var body = Section(bundle, EvidenceKind.File, "Settings.json");
        Assert.Contains("truncated", body);
        Assert.True(body.Length < 205_000);
        Assert.Contains("not installed", bundle.Sections.Single(s => s.Kind == EvidenceKind.Install).Title);
    }

    [Fact]
    public void Directory_listing_has_two_levels_and_at_most_300_entries()
    {
        using var p = new LabProfile();
        for (var i = 0; i < 400; i++) p.Write(p.Documents, $@"My Games\Borderlands 4\Sub{i % 4}\f{i}.ini", "a=1\n");
        var dir = Section(new EvidenceCollector(p.Options()).Collect(p.Target()), EvidenceKind.Directory, "My Games");
        var lines = dir.Split('\n');
        Assert.True(lines.Length <= 302, $"{lines.Length} lines");
        Assert.Contains(lines, l => l.StartsWith("Sub0/f0.ini"));
        Assert.Contains("more entries", dir);
    }

    [Fact]
    public void Collect_does_not_throw_when_everything_is_missing()
    {
        using var p = new LabProfile();
        var opts = new EvidenceOptions
        {
            Folders = new LabFolders(new Dictionary<string, string> { ["Documents"] = Path.Combine(p.Tmp.Path, "nope") }),
            Registry = new LabFakeRegistry(), CloudCheck = _ => throw new InvalidOperationException("boom"),
        };
        var bundle = new EvidenceCollector(opts).Collect(new LabTarget { Name = "", Store = "x", StoreId = "" });
        Assert.NotEmpty(bundle.Notes);
        Assert.Contains(bundle.Notes, n => n.Contains("boom"));
    }

    // ------------------------------------------------------------ splitting

    [Fact]
    public void Split_keeps_every_part_under_60k_and_starts_each_with_the_marker()
    {
        var b = new EvidenceBundle();
        for (var i = 0; i < 12; i++) b.Sections.Add(new EvidenceSection(EvidenceKind.File, $"{{Documents}}\\f{i}.ini", string.Join("\n", Enumerable.Repeat("Key=Value0123456789", 1500))));
        b.Sections.Add(new EvidenceSection(EvidenceKind.File, "{Documents}\\huge.json", string.Join("\n", Enumerable.Repeat("0123456789abcdef", 20_000)))); // 340 KB alone
        b.Notes.Add("note one");
        var parts = b.Split();
        Assert.True(parts.Count > 3);
        Assert.All(parts, part =>
        {
            Assert.True(part.Length <= 60_000, $"part has {part.Length} chars");
            Assert.StartsWith(GameLabContract.EvidenceMarker, part);
        });
        Assert.Contains("(part 1/", string.Concat(parts));
        Assert.Contains("note one", parts[^1]);
        Assert.Contains("_Evidence part 1/" + parts.Count + "_", parts[0]);
    }

    [Fact]
    public void Fences_grow_when_content_contains_backticks()
    {
        var s = new EvidenceSection(EvidenceKind.File, "x", "a ```code``` b");
        Assert.Contains("````\na ```code``` b\n````", s.Render());
    }

    [Fact]
    public void An_empty_bundle_still_makes_one_part()
    {
        var parts = new EvidenceBundle().Split();
        Assert.Single(parts);
        Assert.StartsWith(GameLabContract.EvidenceMarker, parts[0]);
    }

    // ------------------------------------------------------------ needs-evidence allow-list

    [Theory]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"{LocalAppData}\..\..\..\Windows\win.ini")]
    [InlineData(@"{LocalAppData}\Foo\..\..\Bar")]
    [InlineData(@"{UserProfile}\.ssh\id_rsa")]
    [InlineData(@"{UserProfile}")]
    [InlineData(@"{UserProfile}\Desktop\secret.txt")]
    [InlineData(@"{ProgramFiles}\Foo\a.ini")]
    [InlineData(@"{SteamRoot}\config\loginusers.vdf")]
    [InlineData(@"\\server\share\a.ini")]
    [InlineData(@"{LocalAppData}\Google\Chrome\User Data\Default\Login Data")]
    [InlineData(@"{AppData}\Microsoft\Credentials\x")]
    [InlineData(@"{Documents}\Foo\C:\Windows")]
    [InlineData(@"%USERPROFILE%\Documents\a.ini")]
    [InlineData(@"{SteamLibrary:999}\a.ini")]
    [InlineData("")]
    public void Dangerous_paths_are_refused(string pattern)
    {
        using var p = new LabProfile();
        Assert.NotNull(EvidencePolicy.CheckPath(pattern, p.Folders, out _));
    }

    [Theory]
    [InlineData(@"{LocalAppData}\OakGame\Saved\Config\Windows\*.ini")]
    [InlineData(@"{Documents}\My Games\Borderlands 4\**\*.ini")]
    [InlineData(@"{UserProfile}\Documents\x.ini")]
    [InlineData(@"{UserProfile}\AppData\Local\Foo\x.ini")]
    [InlineData(@"{LocalLow}\Gearbox\Borderlands 4\settings.json")]
    [InlineData(@"{SavedGames}\Gearbox")]
    public void Normal_profile_paths_are_allowed(string pattern)
    {
        using var p = new LabProfile();
        Assert.Null(EvidencePolicy.CheckPath(pattern, p.Folders, out var abs));
        Assert.StartsWith(p.Root, abs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_games_own_install_dir_is_allowed_through_its_token_only()
    {
        using var p = new LabProfile();
        var f = p.Folders.WithExtra("SteamLibrary:1285190", p.Install);
        Assert.Null(EvidencePolicy.CheckPath(@"{SteamLibrary:1285190}\OakGame\*.ini", f, out var abs));
        Assert.StartsWith(p.Install, abs);
        Assert.NotNull(EvidencePolicy.CheckPath(@"{SteamLibrary:42}\x.ini", f, out _));
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services", false)]
    [InlineData(@"HKLM\SAM\SAM\Domains", false)]
    [InlineData(@"HKLM\SECURITY", false)]
    [InlineData(@"HKCU\Software", false)]
    [InlineData(@"HKCU\Software\..\..\Foo", false)]
    [InlineData(@"HKCU\Environment", false)]
    [InlineData(@"HKCR\Foo", false)]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", false)]
    [InlineData(@"HKLM\Software\Foo\Cryptography", false)]
    [InlineData(@"HKCU\Software\Gearbox\Borderlands 4", true)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Gearbox", true)]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE/SOFTWARE/Foo", true)]
    public void Registry_requests_are_limited_to_vendor_keys_under_software(string key, bool allowed)
    {
        var why = EvidencePolicy.CheckRegistryKey(key, out var norm);
        Assert.Equal(allowed, why is null);
        if (allowed) Assert.StartsWith("HK", norm);
    }

    [Fact]
    public void CollectRequested_reads_what_is_allowed_and_explains_what_is_refused()
    {
        using var p = new LabProfile();
        p.Write(p.LocalAppData, @"OakGame\Saved\Config\Windows\Engine.ini", "[A]\nb=1\n");
        p.Write(p.LocalAppData, @"OakGame\Saved\Config\Windows\Scalability.ini", "[B]\nc=2\n");
        p.Write(p.LocalAppData, @"OakGame\Saved\Config\Windows\notes.txt", "n\n");
        p.Write(p.Root, @".ssh\id_rsa", "PRIVATE");
        p.Registry.Add(@"HKCU\Software\Gearbox\Borderlands 4", ("Quality", "3"));
        p.Registry.Add(@"HKLM\SYSTEM\Foo", ("secret", "1"));

        var req = new NeedsEvidence(
            [@"{LocalAppData}\OakGame\Saved\Config\Windows\*.ini", @"{UserProfile}\.ssh\id_rsa", @"C:\Windows\win.ini", @"{LocalAppData}\..\..\x"],
            [@"HKCU\Software\Gearbox\Borderlands 4", @"HKLM\SYSTEM\Foo"],
            [@"{LocalAppData}\OakGame", @"C:\Windows"]);
        var collector = new EvidenceCollector(p.Options());
        var bundle = collector.CollectRequested(req, p.Target());

        var files = bundle.Sections.Where(s => s.Kind == EvidenceKind.File).Select(s => s.Title).ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.EndsWith(".ini", f));
        Assert.DoesNotContain("PRIVATE", bundle.Render());
        Assert.Single(bundle.Sections, s => s.Kind == EvidenceKind.Registry);
        Assert.DoesNotContain(@"HKLM\SYSTEM\Foo", p.Registry.Read);      // the refused key was never even opened
        Assert.Single(bundle.Sections, s => s.Kind == EvidenceKind.Directory);
        Assert.Equal(5, bundle.Notes.Count(n => n.Contains("Refused")));
    }

    [Fact]
    public void CollectRequested_does_not_follow_wildcards_into_protected_folders_or_junctions()
    {
        using var p = new LabProfile();
        p.Write(p.AppData, @"Discord\Local Storage\leveldb\000003.log", "token");
        p.Write(p.AppData, @"OkFolder\x.ini", "fine=1\n");
        var bundle = new EvidenceCollector(p.Options()).CollectRequested(new NeedsEvidence([@"{AppData}\*\**\*"], [], []));
        Assert.DoesNotContain("token", bundle.Render());
    }
}
