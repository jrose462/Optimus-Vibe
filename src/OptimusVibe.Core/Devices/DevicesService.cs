using System.Text;
using System.Text.RegularExpressions;
using OptimusVibe.Config;
using OptimusVibe.Display;

namespace OptimusVibe.Devices;

/// <summary>One device of devices.yaml, in file order (first match wins).</summary>
public sealed record DeviceEntry(string Id, DeviceDef Def)
{
    public string DisplayName => DeviceNames.Display(Id, Def);

    /// <summary>"cpu = *Z2 Extreme*, gpu = ..." for lists.</summary>
    public string MatchSummary => Def.Match.Count == 0
        ? "no match rules (only selectable by hand)"
        : string.Join(", ", Def.Match.Select(m => $"{m.Key} = {m.Value}"));
}

/// <summary>What the Add-this-device form starts with, worked out from the hardware of this machine.</summary>
public sealed class DeviceSuggestion
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public Dictionary<string, string> Match { get; init; } = new();
    public DisplayModeDef? Display { get; init; }
    public string Driver { get; init; } = "none";
    public string DefaultPreset { get; init; } = "quality";
    public string? PresetsFrom { get; init; }
    public bool IsHandheld { get; init; }

    /// <summary>The computer name, offered as an optional extra match rule (it tells two identical machines apart).</summary>
    public string MachineName { get; init; } = "";

    /// <summary>Set when an existing device already matches this machine: <c>machine</c> was added to the match rules.</summary>
    public string? ExistingMatch { get; init; }
}

public sealed record DeviceDeleteResult(bool Deleted, string Message);

/// <summary>
/// Reads and edits config/devices.yaml. Edits are text-level: the header comments, per-device comments and the order of
/// the devices survive; only the touched device block is rewritten (a rename only touches its <c>name:</c> line).
/// Ids are immutable because presets (games/*.yaml) and saves (saves/&lt;game&gt;/&lt;id&gt;/) are keyed by them.
/// </summary>
public sealed class DevicesService(AppPaths paths)
{
    private static readonly object Gate = new();
    private static readonly Regex KebabId = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex KeyLine = new(@"^(?<indent> +)(?<key>[A-Za-z0-9][A-Za-z0-9_.\-]*)\s*:\s*(#.*)?$", RegexOptions.Compiled);
    private static readonly string[] Drivers = ["nvidia", "amd", "none"];

    /// <summary>The match keys <see cref="DeviceDetector.Matches"/> understands.</summary>
    public static readonly string[] MatchKeys = ["model", "manufacturer", "cpu", "gpu", "machine"];

    public string FilePath => paths.DevicesFile;

    // ---------------------------------------------------------------- read

    public IReadOnlyList<DeviceEntry> Load()
    {
        lock (Gate) return ToEntries(new ConfigStore(paths).LoadDevices());
    }

    private static List<DeviceEntry> ToEntries(DevicesFile file) =>
        file.Devices.Select(kv => new DeviceEntry(kv.Key, kv.Value ?? new DeviceDef())).ToList();

    // ---------------------------------------------------------------- ids

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    public static string UniqueId(string baseId, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var id = string.IsNullOrEmpty(baseId) ? "device" : baseId;
        if (!taken.Contains(id)) return id;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{id}-{i}")) return $"{id}-{i}";
    }

    /// <summary>Null when the id is fine for a new device; otherwise what is wrong with it.</summary>
    public static string? ValidateId(string? id, IEnumerable<string> existing)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Give the device an id.";
        if (!KebabId.IsMatch(id)) return "The id uses lowercase letters, digits and single dashes (like ally-x).";
        if (existing.Contains(id, StringComparer.OrdinalIgnoreCase)) return $"There is already a device with the id '{id}'.";
        return null;
    }

    // ---------------------------------------------------------------- write

    /// <summary>Adds a device. Goes first when its match rules are more specific than an existing device's (first match wins), else last.</summary>
    public IReadOnlyList<DeviceEntry> Add(string id, string name, IReadOnlyDictionary<string, string>? match, DisplayModeDef? display,
        string? driver, string? defaultPreset, string? presetsFrom)
    {
        lock (Gate)
        {
            var doc = Read();
            var existing = doc.Blocks.Select(b => b.Id).ToList();
            if (ValidateId(id, existing) is { } idError) throw new ConfigException(idError);
            var def = Build(name, match, display, driver, defaultPreset, presetsFrom);
            ValidateDef(id, def, ParseDevices(doc), isNew: true);

            var current = ParseDevices(doc);
            int specificity = def.Match.Count;
            int at = doc.Blocks.FindIndex(b => current.Devices.TryGetValue(b.Id, out var d) && d.Match.Count > 0 && d.Match.Count < specificity);
            if (at < 0) at = doc.Blocks.Count;
            var block = new Block(id, at == 0 ? [] : [""], Render(id, def, doc.Indent));
            doc.Blocks.Insert(at, block);
            if (at == 0 && doc.Blocks.Count > 1 && (doc.Blocks[1].Lead.Count == 0 || doc.Blocks[1].Lead[0].Trim().Length > 0))
                doc.Blocks[1].Lead.Insert(0, "");
            return Write(doc);
        }
    }

    /// <summary>Rewrites one device's settings (everything except its id). Comments above it stay.</summary>
    public IReadOnlyList<DeviceEntry> Update(string id, string name, IReadOnlyDictionary<string, string>? match, DisplayModeDef? display,
        string? driver, string? defaultPreset, string? presetsFrom)
    {
        lock (Gate)
        {
            var doc = Read();
            var block = Find(doc, id);
            var def = Build(name, match, display, driver, defaultPreset, presetsFrom);
            ValidateDef(id, def, ParseDevices(doc), isNew: false);
            block.Body = Render(id, def, doc.Indent);
            return Write(doc);
        }
    }

    /// <summary>Changes only the display name (the <c>name:</c> line); everything else in the block is left exactly as it was.</summary>
    public IReadOnlyList<DeviceEntry> Rename(string id, string name)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ConfigException("Give the device a name.");
            var doc = Read();
            var block = Find(doc, id);
            SetKey(block, doc.Indent, "name", Yaml.Serialize(new Dictionary<string, string> { ["name"] = name.Trim() }).TrimEnd());
            return Write(doc);
        }
    }

    public IReadOnlyList<DeviceEntry> Move(string id, int delta)
    {
        lock (Gate)
        {
            var doc = Read();
            var at = doc.Blocks.FindIndex(b => b.Id == id);
            if (at < 0) throw new ConfigException($"No device '{id}' in devices.yaml.");
            var to = Math.Clamp(at + delta, 0, doc.Blocks.Count - 1);
            if (to == at) return ToEntries(ParseDevices(doc));
            (doc.Blocks[at], doc.Blocks[to]) = (doc.Blocks[to], doc.Blocks[at]);
            return Write(doc);
        }
    }

    /// <summary>
    /// Removes a device. Refused (nothing written) when it is <paramref name="currentId"/> and not <paramref name="confirmed"/>.
    /// Devices that took presets from it lose that link. Presets and saves keyed by the id stay in the repo.
    /// </summary>
    public DeviceDeleteResult Delete(string id, string? currentId, bool confirmed)
    {
        lock (Gate)
        {
            var doc = Read();
            var block = Find(doc, id);
            if (string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase) && !confirmed)
                return new DeviceDeleteResult(false, $"'{id}' is the device this machine is using right now. Confirm to delete it anyway.");
            var devices = ParseDevices(doc);
            var dependents = devices.Devices.Where(kv => string.Equals(kv.Value.PresetsFrom?.Trim(), id, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
            doc.Blocks.Remove(block);
            foreach (var dep in dependents)
                RemoveKey(Find(doc, dep), "presetsFrom");
            Write(doc);
            var msg = $"Deleted '{id}'. Its presets in the game files and its saves stay in the repo.";
            if (dependents.Count > 0) msg += $" {string.Join(", ", dependents)} no longer {(dependents.Count == 1 ? "takes" : "take")} presets from it.";
            return new DeviceDeleteResult(true, msg);
        }
    }

    // ---------------------------------------------------------------- suggestion

    /// <summary>Detects this machine (WMI + current display mode) and proposes a device for it. Slow: call off the UI thread.</summary>
    public DeviceSuggestion SuggestForThisMachine()
    {
        var hw = DeviceDetector.Detect();
        DisplayModeDef? mode = null;
        try
        {
            if (DisplayManager.GetCurrent() is { } cur)
                mode = new DisplayModeDef { Width = cur.Width, Height = cur.Height, Refresh = cur.Refresh };
        }
        catch (Exception) { /* no display info: leave the display out */ }
        DevicesFile existing;
        try { lock (Gate) existing = new ConfigStore(paths).LoadDevices(); }
        catch (Exception) { existing = new DevicesFile(); }
        return Suggest(hw, mode, Environment.MachineName, existing);
    }

    private static readonly Regex HandheldModel = new(
        @"ROG\s*(Xbox\s*)?Ally|\bAlly\s*X\b|Steam\s*Deck|Legion\s*Go|\bClaw\b|Zotac\s*Zone|AYANEO|AYN\b|OneXPlayer|\bGPD\b|\bJupiter\b|\bGalileo\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HandheldCpu = new(@"\bZ[12]\b|Custom APU|Ryzen AI Z", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApuToken = new(@"Z[12](\s+Extreme)?|Custom APU", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GpuModel = new(@"^(?<m>.*?\b(RTX|GTX|GT|RX|Arc|Quadro)\s*[A-Za-z]?\s*\d{3,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Handhelds are matched on the CPU (their APU is unique: a GPU rule like "AMD Radeon*" would also match a desktop's iGPU),
    /// desktops on the GPU model. If an existing device already matches this machine, <c>machine</c> (the computer name) is added so
    /// the new device can be told apart.
    /// </summary>
    public static DeviceSuggestion Suggest(HardwareInfo hw, DisplayModeDef? display, string machineName, DevicesFile existing)
    {
        bool handheld = HandheldModel.IsMatch(hw.Model) || HandheldModel.IsMatch(hw.Manufacturer + " " + hw.Model) || HandheldCpu.IsMatch(hw.Cpu);
        var match = new Dictionary<string, string>();
        string name;

        if (handheld)
        {
            var model = hw.Model.Trim();
            name = model.Length > 0 ? model : (hw.Manufacturer.Trim().Length > 0 ? hw.Manufacturer.Trim() + " handheld" : "Handheld");
            var token = ApuToken.Match(hw.Cpu);
            if (token.Success) match["cpu"] = $"*{token.Value.Trim()}*";
            else if (hw.Cpu.Length > 0) match["cpu"] = hw.Cpu;
            else if (model.Length > 0) match["model"] = $"*{model}*";
        }
        else
        {
            var gpu = PickGpu(hw.Gpus);
            if (gpu is not null)
            {
                var m = GpuModel.Match(gpu);
                match["gpu"] = (m.Success ? m.Groups["m"].Value : gpu) + "*";
            }
            else if (hw.Cpu.Length > 0) match["cpu"] = hw.Cpu;
            name = gpu is null ? "Desktop" : $"Desktop ({ShortGpu(gpu)})";
        }

        string? clash = null;
        foreach (var (id, def) in existing.Devices)
        {
            try
            {
                if (def.Match.Count > 0 && DeviceDetector.Matches(def.Match, hw)) { clash = id; break; }
            }
            catch (ConfigException) { /* a bad rule in the file: ignore for suggestion purposes */ }
        }
        if (clash is not null && machineName.Length > 0) match["machine"] = machineName;

        return new DeviceSuggestion
        {
            Name = name,
            Id = UniqueId(Slug(name), existing.Devices.Keys),
            Match = match,
            Display = display,
            Driver = DeviceDetector.DriverFor(null, hw),
            DefaultPreset = handheld ? "performance" : "quality",
            IsHandheld = handheld,
            MachineName = machineName,
            ExistingMatch = clash,
        };
    }

    private static string? PickGpu(List<string> gpus) =>
        gpus.FirstOrDefault(g => Regex.IsMatch(g, @"\b(RTX|GTX|RX|Arc|Quadro)\b", RegexOptions.IgnoreCase)) ?? gpus.FirstOrDefault();

    private static string ShortGpu(string gpu)
    {
        var m = GpuModel.Match(gpu);
        var s = m.Success ? m.Groups["m"].Value : gpu;
        foreach (var vendor in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) " })
            if (s.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) { s = s[vendor.Length..]; break; }
        return s.Trim();
    }

    // ---------------------------------------------------------------- validation

    private static DeviceDef Build(string name, IReadOnlyDictionary<string, string>? match, DisplayModeDef? display,
        string? driver, string? defaultPreset, string? presetsFrom)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ConfigException("Give the device a name.");
        var def = new DeviceDef
        {
            Name = name.Trim(),
            Display = display is { Width: not null, Height: not null } || display is { Refresh: not null } ? display : null,
            Driver = string.IsNullOrWhiteSpace(driver) ? null : driver.Trim().ToLowerInvariant(),
            DefaultPreset = string.IsNullOrWhiteSpace(defaultPreset) ? null : defaultPreset.Trim(),
            PresetsFrom = string.IsNullOrWhiteSpace(presetsFrom) ? null : presetsFrom.Trim(),
        };
        if (match is not null)
            foreach (var (k, v) in match)
            {
                if (string.IsNullOrWhiteSpace(k) && string.IsNullOrWhiteSpace(v)) continue;
                var key = k.Trim().ToLowerInvariant();
                if (key == "hostname") key = "machine";
                if (!MatchKeys.Contains(key)) throw new ConfigException($"Unknown match key '{k}' (use {string.Join(", ", MatchKeys)}).");
                if (string.IsNullOrWhiteSpace(v)) throw new ConfigException($"The '{key}' match rule needs a value.");
                def.Match[key] = v.Trim();
            }
        return def;
    }

    private static void ValidateDef(string id, DeviceDef def, DevicesFile current, bool isNew)
    {
        if (def.Driver is not null && !Drivers.Contains(def.Driver)) throw new ConfigException($"The driver is one of {string.Join(", ", Drivers)}.");
        if (def.Display is { } d && (d.Width <= 0 || d.Height <= 0 || d.Refresh <= 0)) throw new ConfigException("Display values must be positive.");
        if (def.PresetsFrom is { } from)
        {
            if (string.Equals(from, id, StringComparison.Ordinal)) throw new ConfigException("A device can't take presets from itself.");
            if (!current.Devices.ContainsKey(from)) throw new ConfigException($"'{from}' is not a device in devices.yaml.");
            var seen = new List<string> { id, from };
            for (var next = current.Devices[from].PresetsFrom?.Trim(); !string.IsNullOrEmpty(next); next = current.Devices.GetValueOrDefault(next)?.PresetsFrom?.Trim())
            {
                if (seen.Contains(next, StringComparer.Ordinal))
                    throw new ConfigException($"That would make a loop: {string.Join(" -> ", seen)} -> {next}.");
                seen.Add(next);
            }
        }
    }

    // ---------------------------------------------------------------- text model

    private sealed class Block(string id, List<string> lead, List<string> body)
    {
        public string Id { get; } = id;
        public List<string> Lead { get; } = lead;
        public List<string> Body { get; set; } = body;
    }

    private sealed class Doc
    {
        public List<string> Header { get; } = new();      // everything up to and including the "devices:" line
        public List<Block> Blocks { get; } = new();
        public List<string> Tail { get; } = new();        // comments after the last block
        public int Indent { get; set; } = 2;
        public string Newline { get; set; } = "\n";
    }

    private Doc Read()
    {
        if (!File.Exists(paths.DevicesFile)) throw new ConfigException($"Missing {paths.DevicesFile}");
        var text = File.ReadAllText(paths.DevicesFile);
        var doc = new Doc { Newline = text.Contains("\r\n") ? "\r\n" : "\n" };
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int start = lines.FindIndex(l => Regex.IsMatch(l, @"^devices\s*:\s*(#.*)?$"));
        if (start < 0)
        {
            if (lines.Any(l => Regex.IsMatch(l, @"^devices\s*:\s*\S")))
                throw new ConfigException("devices.yaml uses an inline devices map; edit it by hand.");
            doc.Header.AddRange(lines);
            doc.Header.Add("devices:");
            return doc;
        }
        doc.Header.AddRange(lines.Take(start + 1));

        var pending = new List<string>();
        Block? current = null;
        int? blockIndent = null;
        foreach (var line in lines.Skip(start + 1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { pending.Add(line); continue; }
            var m = KeyLine.Match(line);
            blockIndent ??= m.Success ? m.Groups["indent"].Length : line.Length - line.TrimStart().Length;
            int indent = line.Length - line.TrimStart().Length;
            if (m.Success && indent == blockIndent)
            {
                current = new Block(m.Groups["key"].Value, pending, [line]);
                doc.Blocks.Add(current);
                pending = new List<string>();
                continue;
            }
            if (current is null) throw new ConfigException("devices.yaml has content under devices: that the editor doesn't understand; edit it by hand.");
            current.Body.AddRange(pending);
            current.Body.Add(line);
            pending = new List<string>();
        }
        doc.Tail.AddRange(pending);
        doc.Indent = blockIndent ?? 2;
        return doc;
    }

    private static Block Find(Doc doc, string id) =>
        doc.Blocks.FirstOrDefault(b => b.Id == id) ?? throw new ConfigException($"No device '{id}' in devices.yaml.");

    private static List<string> Render(string id, DeviceDef def, int indent)
    {
        var yaml = Yaml.Serialize(new Dictionary<string, DeviceDef> { [id] = def }).Replace("\r\n", "\n").TrimEnd('\n');
        var pad = new string(' ', indent);
        var scale = indent / 2; // YamlDotNet indents by 2; widen for files that use another indent
        return yaml.Split('\n').Select(l =>
        {
            int lead = l.Length - l.TrimStart().Length;
            return pad + new string(' ', lead * Math.Max(1, scale)) + l.TrimStart();
        }).ToList();
    }

    private static void SetKey(Block block, int indent, string key, string yamlLine)
    {
        int child = ChildIndent(block, indent);
        var pad = new string(' ', child);
        var rx = new Regex($@"^ {{{child}}}{Regex.Escape(key)}\s*:");
        var at = block.Body.FindIndex(l => rx.IsMatch(l));
        if (at >= 0) block.Body[at] = pad + yamlLine;
        else block.Body.Insert(1, pad + yamlLine);
    }

    private static void RemoveKey(Block block, string key)
    {
        for (int i = 1; i < block.Body.Count; i++)
        {
            var l = block.Body[i];
            if (Regex.IsMatch(l, $@"^\s+{Regex.Escape(key)}\s*:")) { block.Body.RemoveAt(i); return; }
        }
    }

    private static int ChildIndent(Block block, int indent)
    {
        foreach (var l in block.Body.Skip(1))
        {
            var t = l.TrimStart();
            if (t.Length > 0 && !t.StartsWith('#')) return l.Length - t.Length;
        }
        return indent + 2;
    }

    private static string Serialize(Doc doc)
    {
        var lines = new List<string>(doc.Header);
        foreach (var b in doc.Blocks)
        {
            lines.AddRange(b.Lead);
            lines.AddRange(b.Body);
        }
        lines.AddRange(doc.Tail);
        return string.Join(doc.Newline, lines) + doc.Newline;
    }

    private static DevicesFile ParseDevices(Doc doc) => Yaml.Parse<DevicesFile>(Serialize(doc));

    /// <summary>Re-parses what is about to be written (a mistake never leaves an unreadable devices.yaml), then replaces the file atomically.</summary>
    private IReadOnlyList<DeviceEntry> Write(Doc doc)
    {
        var text = Serialize(doc);
        DevicesFile parsed;
        try { parsed = Yaml.Parse<DevicesFile>(text); }
        catch (Exception ex) { throw new ConfigException($"That edit would make devices.yaml unreadable ({ex.Message}); nothing was written."); }

        var tmp = paths.DevicesFile + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, paths.DevicesFile, overwrite: true);
        return ToEntries(parsed);
    }
}
