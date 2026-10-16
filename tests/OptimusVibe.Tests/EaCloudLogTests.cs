using OptimusVibe.CloudSync;

namespace OptimusVibe.Tests;

public sealed class EaCloudLogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private const string SavedGames = @"C:\Users\someone\Saved Games\Respawn\JediSurvivor";

    // Synthetic lines modeled on EADesktop.log: tab-separated timestamp / PID / TID / level / source / message.
    internal static string Line(string time, string message, string source = "eax::services::cloudsync::retrieveLocalState") =>
        $"[{time}]\tPID:  3164\tTID: 38260\tINFO    \t({source})\t{message} ";

    internal static string FileLine(string time, string path, int size = 100) =>
        Line(time, $"[CloudSync] New file added to the LocalState filesMap. File path [{path}], file size [{size}], LocalState totalSize [{size}]");

    private static Dictionary<string, DateTime?> Parse(params string[] lines)
    {
        var map = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        EaCloudLog.Parse(new StringReader(string.Join("\n", lines)), map);
        return map;
    }

    [Fact]
    public void Parse_PicksFilePathsFromCloudSyncLinesOnly()
    {
        var map = Parse(
            Line("2026-02-06T03:21:03.314Z", "[CloudSync] Computed RemoteState: Lock [####], rootPath [https://x/198300_], Number of files [5], SnapshotId [abc]"),
            FileLine("2026-02-06T03:21:03.327Z", SavedGames + @"\Backup00.sav"),
            FileLine("2026-02-06T03:21:03.339Z", SavedGames + @"\GameUserSettings.sav", 8360),
            Line("2026-02-06T03:21:03.400Z", "Something else. File path [C:\\not\\cloud.txt], file size [1]", "eax::other"),
            Line("2026-02-06T03:21:05.286Z", "Cloud sync completed successfully, cloudId: [198300_]"));

        Assert.Equal(2, map.Count);
        Assert.Contains(SteamCloud.Norm(SavedGames + @"\GameUserSettings.sav"), map.Keys);
        Assert.DoesNotContain(map.Keys, k => k.Contains("cloud.txt"));
    }

    [Fact]
    public void Parse_KeepsLatestTimestampPerFile()
    {
        var path = SavedGames + @"\GameUserSettings.sav";
        var map = Parse(FileLine("2026-08-20T20:25:11.126Z", path), FileLine("2026-02-06T03:21:03.327Z", path));
        var seen = Assert.Single(map).Value;
        Assert.NotNull(seen);
        Assert.Equal(new DateTime(2026, 8, 20, 20, 25, 11, 126, DateTimeKind.Utc), seen!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parse_ToleratesALineNumberPrefix()
    {
        // the real EADesktop.log has a short counter and a tab in front of each timestamp
        var path = SavedGames + @"\GameUserSettings.sav";
        var map = Parse("   322\t" + FileLine("2025-10-12T17:38:35.927Z", path));
        Assert.Equal(new DateTime(2025, 10, 12, 17, 38, 35, 927, DateTimeKind.Utc), Assert.Single(map).Value!.Value.ToUniversalTime());
    }

    [Fact]
    public void LogFiles_CurrentAndRotated_NotVerboseOrOtherLogs()
    {
        var dir = _tmp.Dir("Logs");
        foreach (var n in new[] { "EADesktop.log", "EADesktop.bak", "EADesktopVerbose.log", "EADesktopVerbose.bak", "EALauncher.log", "EADesktop.exe.txt" })
            File.WriteAllText(Path.Combine(dir, n), "");
        var names = EaCloudLog.LogFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["EADesktop.bak", "EADesktop.log"], names);
        Assert.Empty(EaCloudLog.LogFiles(Path.Combine(dir, "missing")));
    }

    [Fact]
    public void ReadSyncedFiles_CombinesRotatedLogs()
    {
        var dir = _tmp.Dir("Logs");
        File.WriteAllText(Path.Combine(dir, "EADesktop.bak"), FileLine("2026-02-06T03:21:03.327Z", SavedGames + @"\Old.sav"));
        File.WriteAllText(Path.Combine(dir, "EADesktop.log"), FileLine("2026-08-20T20:25:11.126Z", SavedGames + @"\GameUserSettings.sav"));
        var map = EaCloudLog.ReadSyncedFiles(EaCloudLog.LogFiles(dir));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Evaluate_ConfiguredFileInLog_Synced()
    {
        var map = Parse(FileLine("2026-08-20T20:25:11.126Z", SavedGames + @"\GameUserSettings.sav"));
        var files = new[]
        {
            new SyncFile("usersav", SavedGames.ToUpperInvariant() + @"\GAMEUSERSETTINGS.SAV", true),
            new SyncFile("gus", @"C:\Users\someone\AppData\Local\SwGame\Saved\Config\WindowsNoEditor\GameUserSettings.ini", true),
        };
        var ev = EaCloudLog.Evaluate(map, files, logsFound: true);
        Assert.Equal(SyncVerdict.Synced, ev.Verdict);
        Assert.Equal(SyncConfidence.High, ev.Confidence);
        Assert.StartsWith("EA app cloud-syncs GAMEUSERSETTINGS.SAV (last seen Aug", ev.Lines.Single());
        Assert.EndsWith("but not GameUserSettings.ini.", ev.Lines.Single());
    }

    [Fact]
    public void Evaluate_OtherFilesInSameFolder_NotSyncedMedium()
    {
        var map = Parse(FileLine("2026-08-20T20:25:11.126Z", SavedGames + @"\SaveGame00.sav"));
        var ev = EaCloudLog.Evaluate(map, [new SyncFile("opts", SavedGames + @"\Options.cfg", true)], true);
        Assert.Equal(SyncVerdict.NotSynced, ev.Verdict);
        Assert.Equal(SyncConfidence.Medium, ev.Confidence);
    }

    [Fact]
    public void Evaluate_NothingForTheGame_OrNoLogs_Unknown()
    {
        var map = Parse(FileLine("2026-08-20T20:25:11.126Z", @"C:\Other\Game\save.sav"));
        Assert.Equal(SyncVerdict.Unknown, EaCloudLog.Evaluate(map, [new SyncFile("a", SavedGames + @"\x.cfg", true)], true).Verdict);
        var none = EaCloudLog.Evaluate(new Dictionary<string, DateTime?>(), [new SyncFile("a", "x", true)], logsFound: false);
        Assert.Equal(SyncVerdict.Unknown, none.Verdict);
        Assert.Contains("not found", none.Lines.Single());
    }
}
