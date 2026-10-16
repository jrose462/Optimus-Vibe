using OptimusVibe.Config;
using OptimusVibe.Engine;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>One game resolved for this device, with its preset catalog. Built off the UI thread.</summary>
public sealed class GameSnapshot
{
    public required string Id { get; init; }
    public required AppSession Session { get; init; }
    public GameContext? Context { get; init; }
    public IReadOnlyList<PresetOption> Recommended { get; init; } = [];
    public IReadOnlyList<PresetOption> Manual { get; init; } = [];
    public string? Error { get; init; }

    public GameDef? Game => Context?.Game;
    public string Name => Context?.Game.Name is { Length: > 0 } n ? n : Id;

    public static GameSnapshot Load(AppSession session, string id)
    {
        try
        {
            var ctx = GameContext.Load(session.Store, id, session.Device);
            return new GameSnapshot
            {
                Id = ctx.Game.Id,
                Session = session,
                Context = ctx,
                Recommended = PresetCatalog.Recommended(ctx.Game, ctx.Device, ctx.Preset),
                Manual = PresetCatalog.Manual(ctx.Store, ctx.Game, ctx.Device, ctx.Preset),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"{id}: {ex.Message}");
            return new GameSnapshot { Id = id, Session = session, Error = ex.Message };
        }
    }
}
