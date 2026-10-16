using GameProfiles.Config;

namespace GameProfiles.App.Services;

/// <summary>
/// Read-modify-write of %LOCALAPPDATA%\GameProfiles\settings.json (GAMEPROFILES_DATA overrides the folder).
/// Serialized so two quick toggles can't lose each other's change. Small file I/O: callers still run it off the UI thread.
/// </summary>
public static class SettingsStore
{
    private static readonly object Gate = new();

    public static string DataDir => AppPaths.DefaultDataDir;

    public static AppSettings Load()
    {
        lock (Gate) return AppPaths.LoadSettings(DataDir);
    }

    public static AppSettings Update(Action<AppSettings> change)
    {
        lock (Gate)
        {
            var s = AppPaths.LoadSettings(DataDir);
            change(s);
            AppPaths.SaveSettings(DataDir, s);
            return s;
        }
    }

    public static Task<AppSettings> LoadAsync() => Task.Run(Load);
    public static Task<AppSettings> UpdateAsync(Action<AppSettings> change) => Task.Run(() => Update(change));
}
