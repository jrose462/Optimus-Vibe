using System.Management;
using System.Text.RegularExpressions;
using OptimusVibe.Config;

namespace OptimusVibe.Devices;

public sealed class HardwareInfo
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Cpu { get; init; } = "";

    /// <summary>Physical GPUs only (virtual display adapters from Apollo/Sunshine/GlideX etc. are filtered out).</summary>
    public List<string> Gpus { get; init; } = new();

    public List<string> IgnoredAdapters { get; init; } = new();
}

public sealed record DeviceSelection(string Name, DeviceDef? Def, string Reason, string Driver)
{
    /// <summary>The name to show people: the device's display name, else its id.</summary>
    public string DisplayName => DeviceNames.Display(Name, Def);
}

/// <summary>Display names for devices; the id is the identity, the name is only for people.</summary>
public static class DeviceNames
{
    /// <summary>"Ally X", "desktop": for friendly sentences ("for this Ally X"). The display name when set, else the id made readable.</summary>
    public static string Friendly(string id, DeviceDef? def) =>
        !string.IsNullOrWhiteSpace(def?.Name) ? def!.Name!.Trim()
        : id.Equals("ally-x", StringComparison.OrdinalIgnoreCase) ? "Ally X" : id.Replace('-', ' ');

    public static string Display(string id, DeviceDef? def) =>
        !string.IsNullOrWhiteSpace(def?.Name) ? def!.Name!.Trim() : id;
}

public static class DeviceDetector
{
    // Virtual adapters that show up next to real GPUs on streaming hosts.
    private static readonly string[] VirtualAdapterHints =
        ["virtual", "sudomaker", "sunshine", "glidex", "parsec", "remote display", "basic display", "idd", "spacedesk"];

    public static HardwareInfo Detect()
    {
        string manufacturer = "", model = "", cpu = "";
        var gpus = new List<string>();
        var ignored = new List<string>();

        foreach (var o in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        {
            manufacturer = o["Manufacturer"]?.ToString()?.Trim() ?? "";
            model = o["Model"]?.ToString()?.Trim() ?? "";
        }
        // Desktop boards report "System Product Name"; the baseboard product is more useful there.
        if (model is "" or "System Product Name" or "To Be Filled By O.E.M.")
        {
            foreach (var o in Query("SELECT Product FROM Win32_BaseBoard"))
                model = o["Product"]?.ToString()?.Trim() ?? model;
        }
        foreach (var o in Query("SELECT Name FROM Win32_Processor"))
            cpu = o["Name"]?.ToString()?.Trim() ?? "";
        foreach (var o in Query("SELECT Name FROM Win32_VideoController"))
        {
            var name = o["Name"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            (IsVirtualAdapter(name) ? ignored : gpus).Add(name);
        }

        return new HardwareInfo { Manufacturer = manufacturer, Model = model, Cpu = cpu, Gpus = gpus, IgnoredAdapters = ignored };
    }

    public static bool IsVirtualAdapter(string name) =>
        VirtualAdapterHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (var o in searcher.Get()) yield return o;
    }

    /// <summary>Override (--profile / settings.json / env) wins; otherwise the first device whose match rules all pass.</summary>
    public static DeviceSelection Select(DevicesFile devices, HardwareInfo hw, string? forced)
    {
        if (!string.IsNullOrEmpty(forced))
        {
            devices.Devices.TryGetValue(forced, out var def);
            return new DeviceSelection(forced, def, "forced", DriverFor(def, hw));
        }

        foreach (var (name, def) in devices.Devices)
        {
            if (def.Match.Count > 0 && Matches(def.Match, hw))
                return new DeviceSelection(name, def, "matched " + string.Join(", ", def.Match.Select(m => $"{m.Key}={m.Value}")),
                    DriverFor(def, hw));
        }

        throw new ConfigException(
            "This machine matches no device in devices.yaml. Run `optimus-vibe detect` and add a match rule, or pass --profile.");
    }

    public static bool Matches(Dictionary<string, string> rules, HardwareInfo hw)
    {
        foreach (var (key, pattern) in rules)
        {
            bool ok = key.ToLowerInvariant() switch
            {
                "model" => Wildcard(pattern, hw.Model),
                "manufacturer" => Wildcard(pattern, hw.Manufacturer),
                "cpu" => Wildcard(pattern, hw.Cpu),
                "gpu" => hw.Gpus.Any(g => Wildcard(pattern, g)),
                "machine" or "hostname" => Wildcard(pattern, Environment.MachineName),
                _ => throw new ConfigException($"Unknown match key '{key}' (use model, manufacturer, cpu, gpu, machine)"),
            };
            if (!ok) return false;
        }
        return true;
    }

    public static bool Wildcard(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase);

    public static string DriverFor(DeviceDef? def, HardwareInfo hw)
    {
        if (!string.IsNullOrEmpty(def?.Driver)) return def.Driver.ToLowerInvariant();
        // A discrete NVIDIA card wins over an AMD iGPU (e.g. Ryzen desktop with RTX).
        if (hw.Gpus.Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) return "nvidia";
        if (hw.Gpus.Any(g => g.Contains("AMD", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon", StringComparison.OrdinalIgnoreCase))) return "amd";
        return "none";
    }
}
