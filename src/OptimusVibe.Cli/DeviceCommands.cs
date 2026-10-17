using System.CommandLine;
using OptimusVibe.Config;
using OptimusVibe.Devices;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe;

/// <summary>
/// `devices list | add | rename | remove`: edit config\devices.yaml (comments and order are kept; ids are immutable).
/// Registered in Program.Main.
/// </summary>
internal static class DeviceCommands
{
    private static AppPaths Paths(ParseResult pr) => AppPaths.Resolve(pr.GetValue<string?>("--config"));

    /// <summary>Same contract as Program.Guard: config errors print one clean line (exit 2), anything else exit 1.</summary>
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

    public static Command DevicesCommand()
    {
        var cmd = new Command("devices", "List, add, rename and remove devices in devices.yaml.");
        cmd.Subcommands.Add(ListCmd());
        cmd.Subcommands.Add(AddCmd());
        cmd.Subcommands.Add(RenameCmd());
        cmd.Subcommands.Add(RemoveCmd());
        // `devices` alone behaves like `devices list`
        cmd.SetAction(Guard(pr => List(pr)));
        return cmd;
    }

    private static string? CurrentDeviceId(ParseResult pr, AppPaths paths)
    {
        try { return GameContext.SelectDevice(new ConfigStore(paths), pr.GetValue<string?>("--profile")).Name; }
        catch (Exception) { return null; }
    }

    private static int List(ParseResult pr)
    {
        var paths = Paths(pr);
        var entries = new DevicesService(paths).Load();
        var current = CurrentDeviceId(pr, paths);
        if (entries.Count == 0) { Console.WriteLine("No devices in devices.yaml."); return 0; }
        foreach (var e in entries)
        {
            var star = string.Equals(e.Id, current, StringComparison.OrdinalIgnoreCase) ? "  <- this device" : "";
            Console.WriteLine($"{e.Id,-22} {e.DisplayName,-30} {e.MatchSummary}{(e.Def.PresetsFrom is { Length: > 0 } f ? $"  [presets from {f}]" : "")}{star}");
        }
        return 0;
    }

    private static Command ListCmd()
    {
        var cmd = new Command("list", "Show the devices, in match order (first match wins).");
        cmd.SetAction(Guard(List));
        return cmd;
    }

    private static Command AddCmd()
    {
        var id = new Argument<string?>("id") { Description = "Device id: lowercase kebab-case (ally-x). Cannot change later.", Arity = ArgumentArity.ZeroOrOne };
        var name = new Option<string?>("--name") { Description = "Display name shown in the app." };
        var match = new Option<string[]>("--match") { Description = "Match rule key=value (model, manufacturer, cpu, gpu, machine; * and ? wildcards). Repeatable.", AllowMultipleArgumentsPerToken = true };
        var from = new Option<string?>("--presets-from") { Description = "Device id whose presets this device uses when a game has none of its own." };
        var here = new Option<bool>("--from-this-machine") { Description = "Fill in name, id, match rules, display, driver and default preset from this machine." };
        var driver = new Option<string?>("--driver") { Description = "nvidia | amd | none." };
        var defaultPreset = new Option<string?>("--default-preset") { Description = "Preset a game uses on this device until one is picked." };
        var cmd = new Command("add", "Add a device to devices.yaml.") { id, name, match, from, here, driver, defaultPreset };
        cmd.SetAction(Guard(pr =>
        {
            var service = new DevicesService(Paths(pr));
            DeviceSuggestion? s = pr.GetValue(here) ? service.SuggestForThisMachine() : null;

            var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in pr.GetValue(match) ?? [])
            {
                var eq = m.IndexOf('=');
                if (eq <= 0) throw new ConfigException($"--match '{m}' must look like key=value");
                rules[m[..eq].Trim()] = m[(eq + 1)..].Trim();
            }
            if (rules.Count == 0 && s is not null) foreach (var (k, v) in s.Match) rules[k] = v;

            var deviceName = pr.GetValue(name) ?? s?.Name;
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ConfigException("--name is required (or use --from-this-machine).");
            var deviceId = pr.GetValue(id) ?? s?.Id ?? DevicesService.Slug(deviceName);

            service.Add(deviceId, deviceName, rules, s?.Display, pr.GetValue(driver) ?? s?.Driver, pr.GetValue(defaultPreset) ?? s?.DefaultPreset,
                pr.GetValue(from) ?? s?.PresetsFrom);
            Console.WriteLine($"Added {deviceId} ({deviceName}). Run `optimus-vibe sync` to share it with your other machines.");
            if (s?.ExistingMatch is { } clash)
                Console.WriteLine($"Note: '{clash}' already matched this machine, so a machine rule was added to tell them apart.");
            return 0;
        }));
        return cmd;
    }

    private static Command RenameCmd()
    {
        var id = new Argument<string>("id") { Description = "Device id" };
        var name = new Argument<string>("name") { Description = "New display name (the id never changes)" };
        var cmd = new Command("rename", "Change a device's display name.") { id, name };
        cmd.SetAction(Guard(pr =>
        {
            new DevicesService(Paths(pr)).Rename(pr.GetValue(id)!, pr.GetValue(name)!);
            Console.WriteLine($"Renamed {pr.GetValue(id)} to \"{pr.GetValue(name)}\".");
            return 0;
        }));
        return cmd;
    }

    private static Command RemoveCmd()
    {
        var id = new Argument<string>("id") { Description = "Device id" };
        var yes = new Option<bool>("--yes") { Description = "Also remove the device this machine is using right now." };
        var cmd = new Command("remove", "Remove a device from devices.yaml (its presets and saves stay in the repo).") { id, yes };
        cmd.SetAction(Guard(pr =>
        {
            var paths = Paths(pr);
            var result = new DevicesService(paths).Delete(pr.GetValue(id)!, CurrentDeviceId(pr, paths), pr.GetValue(yes));
            Console.WriteLine(result.Message);
            return result.Deleted ? 0 : 2;
        }));
        return cmd;
    }
}
