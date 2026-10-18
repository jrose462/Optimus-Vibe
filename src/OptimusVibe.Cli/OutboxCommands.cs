using System.CommandLine;
using OptimusVibe.Config;
using OptimusVibe.GameLab;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;
using OptimusVibe.Sync;

namespace OptimusVibe;

/// <summary>
/// `outbox list|send`: bug reports and game-lab requests that were saved while offline. The app sends them by itself when it is
/// running and online; `outbox send` does it from the command line. Registered in Program.Main.
/// </summary>
internal static class OutboxCommands
{
    public const string OfflineQueued = "Offline: queued, will be sent next time the app or `optimus-vibe outbox send` runs.";

    private static Func<ParseResult, int> Guard(Func<ParseResult, int> action) => pr =>
    {
        try
        {
            return action(pr);
        }
        catch (Exception ex) when (ex is ConfigException or PathResolveException or GitHubException)
        {
            Log.Error(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected error", ex);
            return 1;
        }
    };

    public static Command OutboxCommand() => new("outbox", "Reports and game-lab requests saved while offline, waiting to be sent.")
    {
        ListCommand(), SendCommand(),
    };

    private static Command ListCommand()
    {
        var cmd = new Command("list", "List what is waiting to be sent, oldest first.");
        cmd.SetAction(Guard(_ =>
        {
            var items = new OutboxStore().List();
            if (items.Count == 0)
            {
                Console.WriteLine("Nothing is waiting to be sent.");
                return 0;
            }
            foreach (var i in items)
            {
                var kind = i.Kind == OutboxKind.BugReport ? "report" : "lab";
                Console.WriteLine($"{i.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {kind,-6}  {i.Summary}");
                if (!string.IsNullOrWhiteSpace(i.LastError)) Console.WriteLine($"    last error ({i.Attempts} attempt(s)): {i.LastError}");
            }
            return 0;
        }));
        return cmd;
    }

    private static Command SendCommand()
    {
        var cmd = new Command("send", "Send what is waiting now (needs a network connection; lab requests need the GitHub sign-in).");
        cmd.SetAction(Guard(_ =>
        {
            var store = new OutboxStore();
            if (store.Count == 0)
            {
                Console.WriteLine("Nothing is waiting to be sent.");
                return 0;
            }
            string? Token()
            {
                try { return GitHubAuth.LoadToken(); }
                catch (Exception) { return null; }
            }
            var send = OutboxFlow.CreateSender(Token, (repo, branch) => Token() is { } t ? new GameLabClient(new GitHubApi(t), repo, branch) : null);
            var r = new OutboxSender(store).DrainAsync(send).GetAwaiter().GetResult();
            if (r.Busy)
            {
                Console.WriteLine("Another send is already running (the app?). Try again in a moment.");
                return 1;
            }
            foreach (var s in r.Sent) Console.WriteLine($"Sent: {s.Item.Summary} {s.Result}");
            foreach (var f in r.Failed) Console.WriteLine($"Failed: {f.Item.Summary}: {f.Error}");
            if (r.StillOffline) Console.WriteLine("Still offline: the rest stays queued.");
            Console.WriteLine($"{r.Sent.Count} sent, {r.Failed.Count} failed, {r.Remaining} still waiting.");
            return r.Failed.Count > 0 ? 1 : 0;
        }));
        return cmd;
    }
}
