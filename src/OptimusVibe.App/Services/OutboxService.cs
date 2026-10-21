using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using OptimusVibe.App.ViewModels;
using OptimusVibe.GameLab;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;
using OptimusVibe.Sync;

namespace OptimusVibe.App.Services;

/// <summary>
/// The app's one offline outbox: the shared <see cref="OutboxStore"/> and the drain (send what is waiting). A drain is triggered at
/// start-up, when the network comes back (debounced), after a GitHub sign-in, by the user ("Send now") and every five minutes while
/// something is waiting. Two drains never run at once (<see cref="OutboxSender"/> returns Busy). Call <see cref="Start"/> on the UI thread.
/// </summary>
public sealed class OutboxService : IDisposable
{
    private static readonly TimeSpan NetworkDebounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer = new() { Interval = RetryInterval };
    private CancellationTokenSource? _debounce;
    private bool _started;
    private int _draining;

    public OutboxService(MainViewModel main)
    {
        _main = main;
        Store = new OutboxStore();
        _timer.Tick += (_, _) => { if (Store.Count > 0) _ = DrainAsync(manual: false); };
    }

    /// <summary>The one store (default data folder, shared with the CLI).</summary>
    public OutboxStore Store { get; }

    /// <summary>The outbox contents changed (an item was added, sent or discarded). Raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Something new was queued (call after <see cref="OutboxFlow"/> reported a queued result).</summary>
    public void NotifyQueued() => RaiseChanged();

    public void RaiseChanged()
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(() => Changed?.Invoke());
        else Changed?.Invoke();
    }

    /// <summary>Hook the triggers and run the first drain. Idempotent.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _timer.Start();
        RaiseChanged();
        if (Store.Count > 0) _ = DrainAsync(manual: false);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleDrain();

    /// <summary>A debounced (about 5 s) drain: network events come in bursts.</summary>
    public void ScheduleDrain()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(NetworkDebounce, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var d = Application.Current?.Dispatcher;
            if (d is null) return;
            await d.InvokeAsync(() => Store.Count > 0 ? DrainAsync(manual: false) : Task.CompletedTask).Task.Unwrap();
        });
    }

    /// <summary>
    /// Sends what is waiting. <paramref name="manual"/> (the "Send now" button) also reports "still offline", failures and "nothing waiting";
    /// automatic drains only announce what was sent. Must be called on the UI thread.
    /// </summary>
    public async Task DrainAsync(bool manual)
    {
        if (GameLabService.IsDemo) return;
        if (Store.Count == 0)
        {
            if (manual) _main.Notify("Nothing is waiting to be sent.");
            return;
        }
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            if (manual) _main.Notify("Already sending...");
            return;
        }
        try
        {
            // automatic findings wait here only while the user still allows them: consent withdrawn since, they are not sent
            try
            {
                if (!AutoFindings.ConsentFrom(await SettingsStore.LoadAsync()) && AutoFindings.WithdrawQueued(Store) > 0) RaiseChanged();
            }
            catch (Exception ex) { Log.Warn($"outbox: could not check the waiting automatic requests: {ex.Message}"); }
            if (Store.Count == 0) return;
            var send = OutboxFlow.CreateSender(SafeToken, LabClientFor);
            var result = await Task.Run(() => new OutboxSender(Store).DrainAsync(send)).ConfigureAwait(true);
            RaiseChanged();
            if (result.Busy)
            {
                if (manual) _main.Notify("Already sending...");
                return;
            }
            foreach (var f in result.Failed) Log.Warn($"outbox: {f.Item.Kind} {f.Item.Id} could not be sent: {f.Error}");
            if (result.Sent.Count > 0)
            {
                _main.Notify(result.Sent.Count == 1 ? "Sent 1 waiting item." : $"Sent {result.Sent.Count} waiting items.");
                if (result.Sent.Any(s => s.Item.Kind == OutboxKind.LabRequest)) _main.Lab.TriggerCycle(force: true);
            }
            if (!manual) return;
            if (result.Failed.Count > 0)
                _main.Notify($"{result.Failed.Count} waiting item(s) couldn't be sent: {result.Failed[0].Error}", error: true);
            else if (result.StillOffline)
                _main.Notify($"Still offline. {result.Remaining} waiting; they will be sent when you are back online.");
        }
        catch (Exception ex)
        {
            Log.Error("Sending the outbox failed", ex);
            if (manual) _main.Notify($"Couldn't send the waiting items: {ex.Message}", error: true);
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
        }
    }

    private static string? SafeToken()
    {
        try { return GitHubAuth.LoadToken(); }
        catch (Exception) { return null; }
    }

    /// <summary>A client for the repo a queued request was made for, signed in with the current GitHub token; null when signed out.</summary>
    private static GameLabClient? LabClientFor(string repo, string branch)
    {
        var token = SafeToken();
        return token is null ? null : new GameLabClient(new GitHubApi(token), repo, branch);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _timer.Stop();
        Interlocked.Exchange(ref _debounce, null)?.Cancel();
    }
}
