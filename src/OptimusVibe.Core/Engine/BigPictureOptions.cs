using OptimusVibe.Config;
using OptimusVibe.Display;

namespace OptimusVibe.Engine;

/// <summary>
/// Pass to <see cref="GameContext.Load(ConfigStore, string, Devices.DeviceSelection, PresetRef?, PathResolver?, BigPictureOptions?)"/>
/// to let a detected TV (docked 4K display or 4K stream client) pick the device's Big Picture preset for games that have it.
/// Not passing it keeps the plain "explicit, remembered, default" order.
/// </summary>
public sealed class BigPictureOptions
{
    /// <summary>`--no-big-picture`: detect nothing, change nothing.</summary>
    public bool Suppress { get; init; }

    /// <summary>Detection override (tests); default = the machine's real displays and environment.</summary>
    public Func<DeviceDef?, BigPictureResult>? Detect { get; init; }
}
