using OptimusVibe.Config;
using OptimusVibe.Devices;

namespace OptimusVibe.Engine;

/// <summary>One choosable entry in the app's Recommended or Manual tab.</summary>
public sealed class PresetOption
{
    public required PresetRef Ref { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public int? TargetFps { get; init; }
    public bool IsActive { get; init; }

    /// <summary>The device's default recommended preset (used when nothing is picked).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Set for manual saves.</summary>
    public ManualSave? Save { get; init; }

    public string? Error { get; init; }
}

public static class PresetCatalog
{
    public static IReadOnlyList<PresetOption> Recommended(GameDef game, DeviceSelection device, PresetRef? active)
    {
        var defaultName = GameContext.DefaultFor(game, device);
        var list = new List<PresetOption>();
        foreach (var name in ProfileResolver.PresetNames(game, device.Name, device.Def))
        {
            var r = PresetRef.Recommended(name);
            try
            {
                var p = ProfileResolver.ResolvePreset(game, device.Name, device.Def, name);
                list.Add(new PresetOption
                {
                    Ref = r, Label = p.Label ?? name, Description = p.Description, TargetFps = p.TargetFps,
                    IsActive = r == active, IsDefault = name == defaultName,
                });
            }
            catch (ConfigException ex)
            {
                list.Add(new PresetOption { Ref = r, Label = name, IsActive = r == active, Error = ex.Message });
            }
        }
        return list;
    }

    public static IReadOnlyList<PresetOption> Manual(ConfigStore store, GameDef game, DeviceSelection device, PresetRef? active) =>
        store.ListSaves(game.Id, device.Name)
            .Select(s => new PresetOption
            {
                Ref = PresetRef.Manual(s.Slug),
                Label = s.Name,
                Description = s.Notes,
                Save = s,
                IsActive = PresetRef.Manual(s.Slug) == active,
            })
            .ToList();
}
