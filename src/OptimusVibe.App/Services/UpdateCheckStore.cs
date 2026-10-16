using System.IO;
using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>When the silent startup update check last ran: update-check.json in the data folder. Never throws.</summary>
public static class UpdateCheckStore
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private sealed class Data
    {
        public DateTimeOffset? LastCheck { get; set; }
    }

    private static string FilePath => Path.Combine(AppPaths.DefaultDataDir, "update-check.json");

    public static DateTimeOffset? LastCheck()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath))?.LastCheck : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"update-check.json: {ex.Message}");
            return null;
        }
    }

    public static bool IsDue() => LastCheck() is not { } last || DateTimeOffset.UtcNow - last >= Interval || last > DateTimeOffset.UtcNow;

    public static void MarkChecked()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DefaultDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data { LastCheck = DateTimeOffset.UtcNow }));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't write update-check.json: {ex.Message}");
        }
    }
}
