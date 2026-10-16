using System.Text;
using System.Text.RegularExpressions;
using OptimusVibe.Logging;

namespace OptimusVibe.Library.Scanners;

/// <summary>
/// Battle.net: C:\ProgramData\Battle.net\Agent\product.db (protobuf: uid, product code, install path, installed flag)
/// merged with the Blizzard entries in the HKLM Uninstall keys (display names; "--uid=" in the uninstall string).
/// StoreId = the Battle.net launch code (Pro, Fen, WoW...) from a uid table. Launch: Battle.net.exe --exec="launch &lt;code&gt;".
/// </summary>
public sealed partial class BattleNetScanner : IGameScanner
{
    /// <summary>Agent uid → (launch code for --exec="launch X", display name).</summary>
    internal static readonly IReadOnlyDictionary<string, (string Code, string Name)> Products =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["prometheus"] = ("Pro", "Overwatch 2"),
            ["fenris"] = ("Fen", "Diablo IV"),
            ["wow"] = ("WoW", "World of Warcraft"),
            ["wow_classic"] = ("WoWC", "World of Warcraft Classic"),
            ["diablo3"] = ("D3", "Diablo III"),
            ["hs_beta"] = ("WTCG", "Hearthstone"),
            ["s2"] = ("S2", "StarCraft II"),
            ["s1"] = ("S1", "StarCraft"),
            ["heroes"] = ("Hero", "Heroes of the Storm"),
            ["osi"] = ("OSI", "Diablo II: Resurrected"),
            ["w3"] = ("W3", "Warcraft III: Reforged"),
            ["anbs"] = ("ANBS", "Diablo Immortal"),
            ["gryphon"] = ("GRY", "Warcraft Rumble"),
            ["odin"] = ("ODIN", "Call of Duty: Modern Warfare"),
            ["viper"] = ("VIPR", "Call of Duty: Black Ops 4"),
            ["zeus"] = ("ZEUS", "Call of Duty: Black Ops Cold War"),
            ["fore"] = ("FORE", "Call of Duty: Vanguard"),
            ["auks"] = ("AUKS", "Call of Duty"),
            ["lazr"] = ("LAZR", "Call of Duty: Modern Warfare 2 Campaign Remastered"),
            ["wlby"] = ("WLBY", "Crash Bandicoot 4: It's About Time"),
            ["rtro"] = ("RTRO", "Blizzard Arcade Collection"),
        };

    /// <summary>Agent entries that are not games.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "agent", "battle.net", "bna", "bts" };

    private const string DefaultBattleNetExe = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";

    private readonly IRegistryReader _registry;
    private readonly string _productDb;
    private readonly string _defaultExe;

    public BattleNetScanner() : this(WindowsRegistryReader.Instance,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent", "product.db"),
        DefaultBattleNetExe) { }

    internal BattleNetScanner(IRegistryReader registry, string productDbPath, string defaultBattleNetExe)
    {
        _registry = registry;
        _productDb = productDbPath;
        _defaultExe = defaultBattleNetExe;
    }

    public string Store => "battlenet";

    [GeneratedRegex(@"--uid=""?([^""\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UidRegex();

    public IEnumerable<DetectedGame> Scan()
    {
        // uid → (install dir, display name, product code from product.db)
        var installs = new Dictionary<string, (string? Dir, string? Name, string? ProductCode)>(StringComparer.OrdinalIgnoreCase);
        string? battleNetDir = null;

        try
        {
            if (File.Exists(_productDb))
            {
                foreach (var p in ProductDb.Parse(File.ReadAllBytes(_productDb)))
                {
                    if (p.Uid.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) battleNetDir ??= p.InstallPath;
                    if (NotGames.Contains(p.Uid) || !p.Installed) continue;
                    installs[p.Uid] = (p.InstallPath, null, p.ProductCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Battle.net: could not read {_productDb}: {ex.Message}");
        }

        List<UninstallEntry> blizzard = [];
        try
        {
            blizzard = UninstallEntry.Read(_registry)
                .Where(e => e.Publisher?.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Battle.net: could not read the uninstall registry: {ex.Message}");
        }

        foreach (var e in blizzard)
        {
            var uid = e.UninstallString is null ? null : UidRegex().Match(e.UninstallString) is { Success: true } m ? m.Groups[1].Value : null;
            if (string.Equals(e.DisplayName, "Battle.net", StringComparison.OrdinalIgnoreCase) || (uid is not null && NotGames.Contains(uid)))
            {
                battleNetDir ??= e.InstallLocation;
                continue;
            }
            uid ??= Products.FirstOrDefault(p => p.Value.Name.Equals(e.DisplayName, StringComparison.OrdinalIgnoreCase)).Key;
            if (uid is null) continue;
            if (installs.TryGetValue(uid, out var existing))
                installs[uid] = (existing.Dir ?? e.InstallLocation, e.DisplayName, existing.ProductCode);
            else
                installs[uid] = (e.InstallLocation, e.DisplayName, null);
        }

        var battleNetExe = battleNetDir is not null && File.Exists(Path.Combine(battleNetDir, "Battle.net.exe"))
            ? Path.GetFullPath(Path.Combine(battleNetDir, "Battle.net.exe"))
            : _defaultExe;

        var result = new List<DetectedGame>();
        foreach (var (uid, info) in installs)
        {
            try
            {
                var dir = ScanUtil.NormalizeDir(info.Dir);
                if (dir is not null && !Directory.Exists(dir))
                {
                    Log.Warn($"Battle.net: {uid} points at a missing folder {dir}; skipped");
                    continue;
                }
                string code, tableName;
                if (Products.TryGetValue(uid, out var known)) (code, tableName) = known;
                else
                {
                    code = info.ProductCode ?? uid;
                    tableName = uid;
                    Log.Warn($"Battle.net: unknown product '{uid}' — launch code guessed as '{code}'");
                }
                result.Add(new DetectedGame
                {
                    Store = Store,
                    StoreId = code,
                    Name = info.Name ?? tableName,
                    InstallDir = dir,
                    LaunchExe = battleNetExe,
                    LaunchArgs = [$"--exec=launch {code}"],
                    Exes = ScanUtil.FindExes(dir),
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Battle.net: skipped {uid}: {ex.Message}");
            }
        }
        return result;
    }

    internal sealed record ProductInstall(string Uid, string? ProductCode, string? InstallPath, bool Installed);

    /// <summary>
    /// Minimal reader for the Battle.net Agent's product.db (protobuf ProductDb): field 1 = repeated ProductInstall
    /// { 1 uid, 2 product_code, 3 settings { 1 install_path }, 4 cached_product_state { 1 base_product_state { 1 installed } } }.
    /// </summary>
    internal static class ProductDb
    {
        public static List<ProductInstall> Parse(byte[] data)
        {
            var result = new List<ProductInstall>();
            foreach (var (field, value) in Fields(data))
            {
                if (field != 1 || value is not byte[] msg) continue;
                string? uid = null, code = null, path = null;
                bool? installed = null;
                foreach (var (f, v) in Fields(msg))
                {
                    switch (f)
                    {
                        case 1 when v is byte[] b: uid = Encoding.UTF8.GetString(b); break;
                        case 2 when v is byte[] b: code = Encoding.UTF8.GetString(b); break;
                        case 3 when v is byte[] b:
                            path = Fields(b).Where(x => x.Field == 1 && x.Value is byte[]).Select(x => Encoding.UTF8.GetString((byte[])x.Value)).FirstOrDefault();
                            break;
                        case 4 when v is byte[] b:
                            var baseState = Fields(b).FirstOrDefault(x => x.Field == 1 && x.Value is byte[]).Value as byte[];
                            if (baseState is not null)
                                installed = Fields(baseState).Where(x => x.Field == 1 && x.Value is ulong).Select(x => (ulong)x.Value != 0).FirstOrDefault();
                            break;
                    }
                }
                if (!string.IsNullOrEmpty(uid)) result.Add(new ProductInstall(uid, code, path, installed ?? true));
            }
            return result;
        }

        /// <summary>Top-level fields of a protobuf message: varints as ulong, length-delimited as byte[]; fixed ints skipped.</summary>
        private static List<(int Field, object Value)> Fields(byte[] data)
        {
            var list = new List<(int, object)>();
            int pos = 0;
            while (pos < data.Length)
            {
                var tag = ReadVarint(data, ref pos);
                var field = (int)(tag >> 3);
                switch ((int)(tag & 7))
                {
                    case 0: list.Add((field, ReadVarint(data, ref pos))); break;
                    case 1: pos += 8; break;
                    case 2:
                        var len = (int)ReadVarint(data, ref pos);
                        if (len < 0 || pos + len > data.Length) throw new FormatException("truncated protobuf");
                        list.Add((field, data[pos..(pos + len)]));
                        pos += len;
                        break;
                    case 5: pos += 4; break;
                    default: throw new FormatException($"unsupported protobuf wire type {tag & 7}");
                }
            }
            return list;
        }

        private static ulong ReadVarint(byte[] data, ref int pos)
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (pos >= data.Length) throw new FormatException("truncated varint");
                var b = data[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("varint too long");
        }
    }
}
