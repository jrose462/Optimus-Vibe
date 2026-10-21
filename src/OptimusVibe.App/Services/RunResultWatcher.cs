using OptimusVibe.GameLab;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>
/// Watches the data dir for <c>run-results.json</c>, which the headless wrapper writes when a game it ran exits (also launches from a Steam
/// launch option, which the app never sees start). Calls <paramref name="changed"/> on a worker thread, debounced: the wrapper writes the run
/// before its capture prompt and again with the answer. Only that file counts (<see cref="PostSession.IsSessionSignal"/>): the per-run
/// <c>state-*.json</c> files and the writers' <c>.tmp</c> files are not sessions. Never throws.
/// </summary>
public sealed class RunResultWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(800);
    private readonly Action _changed;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public RunResultWatcher(string dataDir, Action changed)
    {
        DataDir = dataDir;
        _changed = changed;
        _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            Directory.CreateDirectory(dataDir);
            _watcher = new FileSystemWatcher(dataDir, PostSession.RunResultsFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Renamed += OnEvent; // writers move their temp file over it
            _watcher.Error += (_, e) => Log.Warn($"Watching {PostSession.RunResultsFileName}: {e.GetException().Message}");
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // the app still looks on window activation
            Log.Warn($"Could not watch {dataDir} for finished sessions: {ex.Message}");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public string DataDir { get; }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed || !PostSession.IsSessionSignal(e.FullPath)) return;
        try { _timer.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* closing */ }
    }

    private void Fire()
    {
        if (_disposed) return;
        try { _changed(); }
        catch (Exception ex) { Log.Warn($"After a session finished: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
