using System.CommandLine;
using OptimusVibe.Config;
using OptimusVibe.Display;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe;

/// <summary>
/// `display list` (active displays, to write Big Picture match rules), `tv status` (what the detector sees and why),
/// `tv opt-out|opt-in &lt;game&gt;` (never use Big Picture for a game). Registered in Program.Main.
/// </summary>
internal static class BigPictureCommands
{
    private static AppPaths Paths(ParseResult pr) => AppPaths.Resolve(pr.GetValue<string?>("--config"));

    private static Func<ParseResult, int> Guard(Func<ParseResult, int> action) => pr =>
    {
        try
        {
            return action(pr);
        }
        catch (Exception ex) when (ex is ConfigException or PathResolveException)
        {
            Log.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected error", ex);
            return 1;
        }
    };

    public static Command DisplayCommand()
    {
        var list = new Command("list", "List the active displays: device name, monitor name, mode, primary. Use the name in devices.yaml bigPicture.match.name.");
        list.SetAction(Guard(_ =>
        {
            var displays = DisplayManager.GetActiveDisplays();
            if (displays.Count == 0) Console.WriteLine("No active displays found.");
            foreach (var d in displays)
                Console.WriteLine($"{d.DeviceName,-14} {d.Width}x{d.Height}@{d.Refresh}Hz  {(d.IsPrimary ? "primary" : "       ")}  '{d.FriendlyName}'");
            return 0;
        }));
        return new Command("display", "Inspect displays.") { list };
    }

    public static Command TvCommand()
    {
        var game = new Argument<string>("game") { Description = "Game id" };

        var status = new Command("status", "Show whether Big Picture (TV) is active on this machine, and why.");
        status.SetAction(Guard(pr =>
        {
            var store = new ConfigStore(Paths(pr));
            var device = GameContext.SelectDevice(store, pr.GetValue<string?>("--profile"));
            var displays = DisplayManager.GetActiveDisplays();
            var r = BigPictureDetector.DetectNow(device.Def);
            Console.WriteLine($"device:   {device.Name}");
            Console.WriteLine($"big picture: {(r.Active ? "ACTIVE" : "inactive")} - {r.Reason}");
            Console.WriteLine($"preset:   {r.Preset}");
            Console.WriteLine($"mode:     {r.Mode}");
            if (r.Active) Console.WriteLine($"target:   {r.TargetDisplay ?? "(primary / the stream's virtual display)"}");
            foreach (var d in displays) Console.WriteLine($"display:  {d}");
            foreach (var n in BigPictureDetector.EnvNames)
                if (Environment.GetEnvironmentVariable(n) is { Length: > 0 } v) Console.WriteLine($"env:      {n}={v}");
            return 0;
        }));

        Command Toggle(string name, string help, bool never)
        {
            var c = new Command(name, help) { game };
            c.SetAction(Guard(pr =>
            {
                var s = new ConfigStore(Paths(pr));
                var device = GameContext.SelectDevice(s, pr.GetValue<string?>("--profile"));
                var id = s.LoadGame(pr.GetValue(game)!).Id;
                new SelectionStore(s.Paths.SelectionsFile).SetNeverBigPicture(device.Name, id, never);
                Console.WriteLine($"{id} on {device.Name}: Big Picture {(never ? "will not be used" : "is automatic again")}");
                return 0;
            }));
            return c;
        }

        return new Command("tv", "Big Picture (TV) detection.")
        {
            status,
            Toggle("opt-out", "Never use the Big Picture preset for this game on this device.", true),
            Toggle("opt-in", "Use the Big Picture preset automatically again for this game.", false),
        };
    }
}
