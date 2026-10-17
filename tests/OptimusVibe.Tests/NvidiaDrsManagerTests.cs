using OptimusVibe.Config;
using OptimusVibe.Driver;
using Xunit;

namespace OptimusVibe.Tests;

public class NvidiaDrsManagerTests
{
    private sealed class FakeApi : INvDrsApi
    {
        public bool Available = true;
        public int Sessions, Saves;
        public readonly Dictionary<string, nint> Apps = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<nint, string> Names = new();
        public readonly Dictionary<nint, Dictionary<uint, uint>> Settings = new();
        public readonly Dictionary<uint, uint> Inherited = new();
        public readonly List<string> Writes = new();
        private nint _next = 1;

        public nint AddProfile(string name)
        {
            var h = _next++;
            Names[h] = name;
            Settings[h] = new();
            return h;
        }

        public bool TryInitialize(out string error)
        {
            error = Available ? "" : "NVIDIA driver API (nvapi64.dll) not found; no NVIDIA driver installed.";
            return Available;
        }

        public IDrsSession OpenSession()
        {
            Sessions++;
            return new Session(this);
        }

        private sealed class Session(FakeApi f) : IDrsSession
        {
            public bool FindApplicationProfile(string exe, out nint profile) => f.Apps.TryGetValue(exe, out profile);
            public bool FindProfileByName(string name, out nint profile)
            {
                profile = f.Names.FirstOrDefault(kv => kv.Value == name).Key;
                return profile != 0;
            }
            public nint CreateProfile(string name) { f.Writes.Add($"profile:{name}"); return f.AddProfile(name); }
            public void CreateApplication(nint profile, string exe) { f.Writes.Add($"app:{exe}"); f.Apps[exe] = profile; }
            public string GetProfileName(nint profile) => f.Names[profile];
            public bool TryGetDword(nint profile, uint id, out uint value, out bool inThisProfile)
            {
                if (f.Settings[profile].TryGetValue(id, out value)) { inThisProfile = true; return true; }
                inThisProfile = false;
                return f.Inherited.TryGetValue(id, out value);
            }
            public void SetDword(nint profile, uint id, uint value) { f.Writes.Add($"set:{id:X8}={value}"); f.Settings[profile][id] = value; }
            public void Save() => f.Saves++;
            public void Dispose() { }
        }
    }

    private static GameDef Game(params string[] exes) => new() { Name = "Test Game", Processes = exes.ToList() };
    private static Dictionary<string, string> S(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Unavailable_driver_reports_reason_and_unknown_current()
    {
        var api = new FakeApi { Available = false };
        var m = new NvidiaDrsManager(api);
        Assert.False(m.IsAvailable(out var reason));
        Assert.Contains("nvapi64.dll", reason);
        var plan = m.Plan(Game("a.exe"), S(("FrameRateLimiter", "60")));
        Assert.Equal(new DriverChange("FrameRateLimiter", null, "60"), Assert.Single(plan));
        Assert.Null(m.Apply(Game("a.exe"), S(("FrameRateLimiter", "60"))));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void Plan_reads_current_values_without_writing()
    {
        var api = new FakeApi();
        var p = api.AddProfile("Existing");
        api.Apps["game.exe"] = p;
        api.Settings[p][0x10835002] = 144;
        api.Inherited[0x00A879CF] = 0x08416747;
        var plan = new NvidiaDrsManager(api).Plan(Game("Game.exe"), S(("FrameRateLimiter", "162"), ("VSync", "on"), ("PowerMode", "max")));
        Assert.Equal(new DriverChange("FrameRateLimiter", "144", "162"), plan[0]);
        Assert.Equal(new DriverChange("VSync", "off", "on"), plan[1]);
        Assert.Equal(new DriverChange("PowerMode", null, "max"), plan[2]);
        Assert.Empty(api.Writes);
        Assert.Equal(0, api.Saves);
    }

    [Fact]
    public void Apply_creates_profile_adds_apps_sets_and_saves_once()
    {
        var api = new FakeApi();
        Assert.Null(new NvidiaDrsManager(api).Apply(Game("Game.exe", "Launcher.exe"), S(("FrameRateLimiter", "162"), ("VSync", "off"))));
        Assert.Equal(new[] { "profile:Optimus-Vibe - Test Game", "app:game.exe", "app:launcher.exe", "set:10835002=162", "set:00A879CF=138504007" }, api.Writes);
        Assert.Equal(1, api.Saves);
    }

    [Fact]
    public void Apply_reuses_legacy_profile_name()
    {
        var api = new FakeApi();
        var p = api.AddProfile("GameProfiles - Test Game");
        new NvidiaDrsManager(api).Apply(Game("game.exe"), S(("FrameRateLimiter", "60")));
        Assert.DoesNotContain(api.Writes, w => w.StartsWith("profile:"));
        Assert.Equal(p, api.Apps["game.exe"]);
    }

    [Fact]
    public void Apply_writes_only_differences_and_skips_save_when_unchanged()
    {
        var api = new FakeApi();
        var p = api.AddProfile("Existing");
        api.Apps["game.exe"] = p;
        api.Settings[p][0x10835002] = 162;
        var m = new NvidiaDrsManager(api);
        m.Apply(Game("game.exe"), S(("FrameRateLimiter", "162")));
        Assert.Empty(api.Writes);
        Assert.Equal(0, api.Saves);

        m.Apply(Game("game.exe"), S(("FrameRateLimiter", "162"), ("PowerMode", "max")));
        Assert.Equal(new[] { "set:1057EB71=1" }, api.Writes);
        Assert.Equal(1, api.Saves);
    }

    [Fact]
    public void Inherited_value_is_still_written_into_the_game_profile()
    {
        var api = new FakeApi();
        var p = api.AddProfile("Existing");
        api.Apps["game.exe"] = p;
        api.Inherited[0x10835002] = 162;
        new NvidiaDrsManager(api).Apply(Game("game.exe"), S(("FrameRateLimiter", "162")));
        Assert.Equal(new[] { "set:10835002=162" }, api.Writes);
    }

    [Fact]
    public void Named_settings_values_and_raw_ids_resolve_and_unknown_keys_are_skipped()
    {
        var api = new FakeApi();
        var p = api.AddProfile("P");
        api.Apps["g.exe"] = p;
        var plan = new NvidiaDrsManager(api).Plan(Game("g.exe"), S(
            ("vsyncmode", "ForceOff"), ("PreRenderLimit", "2"), ("0x10835002", "0x20"), ("Nonsense", "1"), ("VSync", "bogus")));
        Assert.Equal(new[] { "VSyncMode", "PreRenderLimit", "0x10835002" }, plan.Select(c => c.Setting));
        Assert.Equal("ForceOff", plan[0].Target);
        Assert.Equal("2", plan[1].Target);
        Assert.Equal("32", plan[2].Target);
    }

    [Theory]
    [InlineData("VSyncMode", 0x00A879CFu)]
    [InlineData("PreferredPerformanceState", 0x1057EB71u)]
    [InlineData("PreRenderLimit", 0x007BA09Eu)]
    public void Table_ids_match_nvapi(string name, uint id) => Assert.Equal(id, NvidiaSettingTable.Ids[name]);

    [Fact]
    public void Real_driver_availability_never_throws()
    {
        var ok = new NvidiaDrsManager().IsAvailable(out var reason);
        Assert.True(ok || reason.Length > 0);
    }
}
