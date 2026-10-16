using GameProfiles.Config;
using GameProfiles.Driver;

namespace GameProfiles.Engine;

/// <summary>What <see cref="ApplyNow.Execute"/> would do for one game — the same steps as the CLI <c>apply</c> verb.</summary>
public sealed class ApplyPreview
{
    public required GameContext Context { get; init; }
    public required List<FilePlan> Files { get; init; }

    /// <summary>nvidia | amd | none</summary>
    public string DriverVendor { get; init; } = "none";

    /// <summary>Null when the driver backend is available (or there is nothing to set).</summary>
    public string? DriverUnavailableReason { get; init; }

    public IReadOnlyList<DriverChange> DriverChanges { get; init; } = [];

    /// <summary>True when driver settings are written by an apply (NVIDIA per-app profiles persist; AMD is run-only).</summary>
    public bool DriverAppliedNow { get; init; }

    /// <summary>The preset's display mode; only the <c>run</c> wrapper applies it (and restores it on exit).</summary>
    public DisplayModeDef? Display { get; init; }

    public bool HasFileWork => Files.Any(f => f.HasWork);
}

/// <summary>
/// "Apply now" without launching (CLI <c>apply</c>): plan file changes and driver settings, then write them.
/// Shared so the app and CLI follow the same rules.
/// </summary>
public static class ApplyNow
{
    public static ApplyPreview Preview(GameContext ctx)
    {
        var plans = GameEngine.Plan(ctx);
        var driver = DriverSettings.For(ctx.Device.Driver);
        string? reason = null;
        IReadOnlyList<DriverChange> changes = [];
        bool appliedNow = false;
        if (ctx.DriverTargets.Count > 0)
        {
            if (!driver.IsAvailable(out var why)) reason = why;
            else
            {
                changes = driver.Plan(ctx.Game, ctx.DriverTargets);
                appliedNow = driver.Vendor == "nvidia";
            }
        }
        return new ApplyPreview
        {
            Context = ctx,
            Files = plans,
            DriverVendor = driver.Vendor,
            DriverUnavailableReason = reason,
            DriverChanges = changes,
            DriverAppliedNow = appliedNow,
            Display = ctx.Profile.Display,
        };
    }

    public static AppliedState Execute(ApplyPreview preview)
    {
        var ctx = preview.Context;
        var state = GameEngine.Apply(ctx, preview.Files);
        if (preview.DriverAppliedNow && ctx.DriverTargets.Count > 0)
            DriverSettings.For(ctx.Device.Driver).Apply(ctx.Game, ctx.DriverTargets);
        return state;
    }
}
