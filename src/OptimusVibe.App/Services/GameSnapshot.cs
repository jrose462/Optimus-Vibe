using OptimusVibe.CloudSync;
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

    /// <summary>Do this game's settings travel between machines? Null when the check could not run.</summary>
    public CloudSyncReport? CloudSync { get; init; }

    public GameDef? Game => Context?.Game;
    public string Name => Context?.Game.Name is { Length: > 0 } n ? n : Id;

    private static CloudSyncReport? CheckSync(AppSession session, GameDef game)
    {
        try { return new CloudSyncChecker(session.Store, session.Device).Check(game); }
        catch (Exception ex)
        {
            Log.Warn($"{game.Id}: cloud check failed: {ex.Message}");
            return null;
        }
    }

    public static GameSnapshot Load(AppSession session, string id)
    {
        try
        {
            var ctx = GameContext.Load(session.Store, id, session.Device, bigPicture: new BigPictureOptions());
            return new GameSnapshot
            {
                Id = ctx.Game.Id,
                Session = session,
                Context = ctx,
                Recommended = PresetCatalog.Recommended(ctx.Game, ctx.Device, ctx.Preset, ctx.BigPictureUsed),
                Manual = PresetCatalog.Manual(ctx.Store, ctx.Game, ctx.Device, ctx.Preset),
                CloudSync = CheckSync(session, ctx.Game),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"{id}: {ex.Message}");
            return new GameSnapshot { Id = id, Session = session, Error = ex.Message };
        }
    }
}
