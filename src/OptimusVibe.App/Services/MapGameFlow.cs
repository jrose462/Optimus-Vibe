using OptimusVibe.App.ViewModels;
using OptimusVibe.GameLab;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>
/// The work behind "Map this game": snapshot, launch the game exactly as it is (in this process, through the runner's mapping mode),
/// snapshot again, and turn the two launches into the lab's mapping evidence. Reads files and registry keys only; nothing is restored.
/// </summary>
public sealed class MapGameFlow(LabTarget target, string dataDir, EvidenceOptions options, bool canSend, Func<Task<string?>> send,
    Func<Task<string?>>? keep, Action gameStarting, Action gameExited) : IMapGameActions
{
    private readonly ProbeSession _session = new(target, options);
    private ProbeSnapshot? _last;
    private ProbeDiff? _passA;
    private ProbeDiff? _passB;
    private ProbeEvidence? _evidence;

    public bool CanSend => canSend;
    public bool CanKeep => keep is not null && _evidence is { OpaqueFiles.Count: > 0 };

    public void GameStarting() => gameStarting();
    public void GameExited() => gameExited();

    public Task<ProbeDiff?> RunPassAsync(int pass) => Task.Run<ProbeDiff?>(() =>
    {
        try
        {
            var before = pass == 1 || _last is null ? _session.Snapshot() : _last;
            Log.Info($"Map this game: {target.Name}: launch {pass}, watching {before.Files.Count} file(s) and {before.Registry.Count} registry key(s)");
            ProbeLauncher.Launch(target, dataDir);
            var after = _session.Snapshot();
            _last = after;
            var diff = ProbeSession.Diff(before, after, _session.Limits);
            if (pass == 1) _passA = diff; else _passB = diff;
            Log.Info($"Map this game: launch {pass} changed {diff.Files.Count} file(s) and {diff.Registry.Count} registry value(s)");
            return diff;
        }
        catch (Exception ex)
        {
            Log.Warn($"Map this game: launch {pass} failed: {ex.Message}");
            return null;
        }
    });

    /// <summary>Builds the evidence and remembers it, so any later request for this game carries it.</summary>
    public ProbeEvidence BuildEvidence()
    {
        _evidence = _session.ToEvidence(_passA ?? new ProbeDiff([], [], []), _passB);
        ProbeStore.Record(dataDir, target.Key, ProbeStore.FromEvidence(_evidence));
        return _evidence;
    }

    public Task<string?> SendAsync() => send();

    public Task<string?> KeepAsync() => keep is null ? Task.FromResult<string?>(null) : keep();
}
