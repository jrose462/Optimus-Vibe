using System.CommandLine;
using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe;

/// <summary>
/// `auto-snapshot &lt;game&gt; [--forget [--preset x]]`: lists (or forgets) this device's own copies of the game's cloud-synced
/// whole files (<c>autoSnapshot</c> in the game yaml). Registered in Program.Main.
/// </summary>
internal static class AutoSnapshotCommands
{
    public static Command AutoSnapshotCommand()
    {
        var game = new Argument<string>("game") { Description = "Game id (file name in config\\games without .yaml)." };
        var forget = new Option<bool>("--forget") { Description = "Delete the stored copies (all of them, or only the --preset one (global option))." };
        var cmd = new Command("auto-snapshot", "List or forget this device's kept copies of cloud-synced settings files (autoSnapshot).") { game, forget };
        cmd.SetAction(pr =>
        {
            try
            {
                var store = new ConfigStore(AppPaths.Resolve(pr.GetValue<string?>("--config")));
                var device = GameContext.SelectDevice(store, pr.GetValue<string?>("--profile"));
                var id = pr.GetValue(game)!;
                var def = store.LoadGame(id);
                var copies = AutoSnapshots.StoreFor(store.Paths);
                var files = def.Files.Where(f => AutoSnapshots.ModeOf(f) is not null).ToList();
                if (files.Count == 0) Console.WriteLine($"{id} has no autoSnapshot files.");

                if (pr.GetValue(forget))
                {
                    var key = pr.GetValue<string?>("--preset") is { Length: > 0 } p ? AutoSnapshots.KeyFromArg(p) : null;
                    int n = copies.Forget(id, device.Name, key);
                    Console.WriteLine(n == 0 ? "Nothing to forget." : $"Forgot {n} copy(ies) for {id} on {device.Name}{(key is null ? "" : $" ({key})")}.");
                    return 0;
                }
                var list = copies.List(id, device.Name);
                if (list.Count == 0) Console.WriteLine($"No copies kept yet for {id} on {device.Name}.");
                foreach (var e in list)
                    Console.WriteLine($"  {e.Meta.Preset,-24} {e.FileId,-16} {e.Meta.Utc.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Meta.Sha256[..Math.Min(12, e.Meta.Sha256.Length)]}{(e.Meta.SessionNote is { } note ? "  (" + note + ")" : "")}");
                return 0;
            }
            catch (Exception ex) when (ex is ConfigException or PathResolveException)
            {
                Log.Error(ex.Message);
                return 2;
            }
        });
        return cmd;
    }

}
