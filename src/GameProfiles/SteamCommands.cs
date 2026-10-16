using System.CommandLine;
using System.Globalization;
using GameProfiles.Config;
using GameProfiles.Engine;
using GameProfiles.Library;
using GameProfiles.Logging;

namespace GameProfiles;

/// <summary>
/// `steam-options &lt;game|appid&gt; [--enable|--disable] [--dry-run] [--exe path] [--exit-steam]` and
/// `steam-options --sync-all [--dry-run] [--exe path] [--exit-steam]`: manage the GameProfilesApp.exe wrapper in Steam
/// launch options (localconfig.vdf of the most recent Steam login).
/// </summary>
internal static class SteamCommands
{
    public static Command SteamOptionsCommand()
    {
        var target = new Argument<string?>("game") { Description = "Game id or Steam app id.", Arity = ArgumentArity.ZeroOrOne };
        var enable = new Option<bool>("--enable") { Description = "Put the wrapper into the game's launch option (keeps your own options)." };
        var disable = new Option<bool>("--disable") { Description = "Remove the wrapper, leaving your own options." };
        var syncAll = new Option<bool>("--sync-all")
        {
            Description = "Enable for every managed Steam game on this device (config with steamAppId and presets, not opted out in library.yaml); disable for opted-out ones.",
        };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the changes without writing." };
        var exe = new Option<string?>("--exe") { Description = "Wrapper exe to write (default: GameProfilesApp.exe next to this exe)." };
        var exitSteam = new Option<bool>("--exit-steam") { Description = "Ask Steam to exit first if it is running (Steam must be closed to write)." };

        var cmd = new Command("steam-options", "Show or change the GameProfiles wrapper in Steam launch options.")
        {
            target, enable, disable, syncAll, dryRun, exe, exitSteam,
        };
        cmd.SetAction(pr =>
        {
            try
            {
                return Execute(pr, pr.GetValue(target), pr.GetValue(enable), pr.GetValue(disable), pr.GetValue(syncAll),
                    pr.GetValue(dryRun), pr.GetValue(exe), pr.GetValue(exitSteam));
            }
            catch (Exception ex) when (ex is ConfigException or PathResolveException or InvalidOperationException)
            {
                Log.Error(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log.Error("Unexpected error", ex);
                return 1;
            }
        });
        return cmd;
    }

    private static int Execute(ParseResult pr, string? target, bool enable, bool disable, bool syncAll, bool dry, string? exeOption, bool exitSteam)
    {
        if (enable && disable) throw new InvalidOperationException("Use either --enable or --disable, not both.");
        if (syncAll && (enable || disable || target is not null))
            throw new InvalidOperationException("--sync-all takes no game and no --enable/--disable.");
        if (!syncAll && target is null)
            throw new InvalidOperationException("Usage: steam-options <game|appid> [--enable|--disable] [--dry-run]  or  steam-options --sync-all [--dry-run]");

        var account = SteamLaunchOptions.AccountDir()
                      ?? throw new InvalidOperationException("Steam user data not found (is Steam installed and signed in once?).");
        Console.WriteLine($"Steam account folder: {account}");
        var store = new ConfigStore(AppPaths.Resolve(pr.GetValue<string?>("--config")));
        var wrapper = Path.GetFullPath(exeOption?.Trim('"') ?? SteamLaunchOptions.DefaultWrapperExe());

        IReadOnlyList<LaunchOptionChange> changes;
        if (syncAll)
        {
            var device = GameContext.SelectDevice(store, pr.GetValue<string?>("--profile"));
            var (on, off) = SteamLaunchOptions.ManagedTargets(store, device.Name);
            Console.WriteLine($"Device: {device.Name}. Managed Steam games: {on.Count}; opted out: {off.Count}");
            CheckWrapper(wrapper, dry);
            changes = SteamLaunchOptions.Plan(on, off, wrapper, account);
        }
        else
        {
            var (appId, gameId) = Resolve(store, target!, needGameId: enable);
            var current = SteamLaunchOptions.Get(appId, account);
            if (!enable && !disable)
            {
                Console.WriteLine($"{appId}{(gameId is null ? "" : $" ({gameId})")}: {(string.IsNullOrEmpty(current) ? "(no launch option)" : current)}");
                Console.WriteLine(SteamLaunchOptions.IsWrapped(current)
                    ? $"  wrapped: runs '{SteamLaunchOptions.WrappedGameId(current)}' via {SteamLaunchOptions.WrappedExe(current)}"
                    : "  not wrapped");
                return 0;
            }
            if (enable) CheckWrapper(wrapper, dry);
            changes = enable
                ? SteamLaunchOptions.Plan([(appId, gameId!)], [], wrapper, account)
                : SteamLaunchOptions.Plan([], [appId], wrapper, account);
        }

        if (changes.Count == 0)
        {
            Console.WriteLine("Launch options already up to date.");
            return 0;
        }
        foreach (var c in changes)
        {
            Console.WriteLine($"  {c.AppId}{(c.GameId is null ? "" : $" ({c.GameId})")}");
            Console.WriteLine($"    before: {c.Before ?? "(none)"}");
            Console.WriteLine($"    after:  {c.After ?? "(none)"}");
        }
        if (dry)
        {
            Console.WriteLine($"Dry run — {changes.Count} change(s), nothing written.");
            return 0;
        }

        if (exitSteam && SteamLaunchOptions.IsSteamRunning())
        {
            Console.WriteLine("Asking Steam to exit…");
            if (!SteamLaunchOptions.ExitSteam(TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException("Steam did not exit within 60 seconds; nothing written.");
        }
        var backup = SteamLaunchOptions.Apply(changes, account);
        Console.WriteLine($"Wrote {changes.Count} change(s). Backup: {backup}");
        return 0;
    }

    private static void CheckWrapper(string wrapper, bool dry)
    {
        if (File.Exists(wrapper)) return;
        var msg = $"Wrapper not found: {wrapper}. Pass --exe \"<path to GameProfilesApp.exe>\".";
        if (dry) Log.Warn(msg);
        else throw new InvalidOperationException(msg);
    }

    /// <summary>App id + game id for a game id or a numeric Steam app id (game id null when no config has that app id).</summary>
    private static (int AppId, string? GameId) Resolve(ConfigStore store, string target, bool needGameId)
    {
        if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
        {
            string? gameId = null;
            foreach (var id in store.GameIds())
            {
                try
                {
                    if (store.LoadGame(id).SteamAppId == appId) { gameId = id; break; }
                }
                catch (Exception) { /* skip broken configs */ }
            }
            if (gameId is null && needGameId)
                throw new ConfigException($"No game config has steamAppId {appId} — add one (or pass the game id) before enabling the wrapper.");
            return (appId, gameId);
        }

        var game = store.LoadGame(target);
        if (game.SteamAppId is not { } id2) throw new ConfigException($"{game.Id} has no steamAppId — it is not a Steam game.");
        return (id2, game.Id);
    }
}
