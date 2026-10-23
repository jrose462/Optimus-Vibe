using System.Globalization;
using System.Text;
using System.Text.Json;
using OptimusVibe.Config;
using OptimusVibe.Logging;

namespace OptimusVibe.Sync;

/// <summary>What <c>github-signin.json</c> holds. Never the token, or any part of it.</summary>
public sealed class SignInRecord
{
    public DateTime? SavedUtc { get; set; }
    /// <summary><see cref="SignInHealth.ViaDevice"/> or <see cref="SignInHealth.ViaToken"/>.</summary>
    public string? SavedVia { get; set; }
    public DateTime? LastOkUtc { get; set; }
    /// <summary>When GitHub first refused the saved sign-in in the current streak; null while it works.</summary>
    public DateTime? RejectedSinceUtc { get; set; }
    /// <summary>X-GitHub-Request-Id of the latest 401 (what GitHub support asks for).</summary>
    public string? LastRejectedRequestId { get; set; }
}

/// <summary>
/// Diagnostics for "GitHub auth expires daily" (issue #85): when the saved token was saved, when GitHub last accepted it and since when it
/// is refused, kept in <c>github-signin.json</c> in the per-machine data dir (written atomically, not synced). Every IO failure is logged
/// and swallowed: this must never break a call.
/// </summary>
public sealed class SignInHealth
{
    public const string ViaDevice = "device sign-in";
    public const string ViaToken = "token";

    /// <summary>Successful calls rewrite the file at most this often (a rejection or a re-sign-in is always written).</summary>
    public static readonly TimeSpan OkWriteInterval = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static SignInHealth? _default;

    /// <summary>The store for this machine's data dir. Replaced by tests.</summary>
    internal static SignInHealth Default
    {
        get => _default ??= new SignInHealth(AppPaths.DefaultDataDir);
        set => _default = value;
    }

    /// <summary>Raised (on the calling thread) the first time GitHub refuses the saved sign-in after it worked or was saved.</summary>
    public static event Action? Rejected;

    /// <summary>Raised when a sign-in is saved or a call succeeds again after a rejection.</summary>
    public static event Action? Recovered;

    private readonly string _file;
    private readonly Func<DateTime> _now;
    private readonly Func<bool> _hasToken;
    private readonly object _gate = new();
    private SignInRecord? _rec;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    /// <summary>Raised like <see cref="Rejected"/>, for this instance only (tests).</summary>
    internal event Action? StreakStarted;

    internal SignInHealth(string dataDir, Func<DateTime>? now = null, Func<bool>? hasToken = null)
    {
        _file = FileFor(dataDir);
        _now = now ?? (() => DateTime.UtcNow);
        _hasToken = hasToken ?? SavedTokenExists;
    }

    public static string FileFor(string dataDir) => Path.Combine(dataDir, "github-signin.json");

    // ------------------------------------------------------------ static entry points (this machine's store)

    public static void RecordSaved(string via) => Default.Saved(via);
    public static void RecordOk() => Default.Ok();
    public static void RecordRejected(string? requestId) => Default.Reject(requestId);
    public static string Describe() => Default.Summary();
    public static bool IsRejected => Default.Rejecting;

    // ------------------------------------------------------------ instance

    /// <summary>True while GitHub refuses the saved sign-in.</summary>
    public bool Rejecting
    {
        get { lock (_gate) return Load().RejectedSinceUtc is not null; }
    }

    /// <summary>A new token was saved after a sign-in: its lifetime starts now and any rejection is over.</summary>
    public void Saved(string via)
    {
        bool recovered;
        lock (_gate)
        {
            var r = Load();
            recovered = r.RejectedSinceUtc is not null;
            r.SavedUtc = _now();
            r.SavedVia = via;
            r.LastOkUtc = r.SavedUtc; // the sign-in was just checked against GitHub
            r.RejectedSinceUtc = null;
            Write(r);
        }
        if (recovered) Log.Info("GitHub sign-in saved again; the rejection is over.");
        Recovered?.Invoke();
    }

    /// <summary>A call got a 2xx answer. Writes at most every <see cref="OkWriteInterval"/>, unless it ends a rejection.</summary>
    public void Ok()
    {
        bool recovered;
        lock (_gate)
        {
            var r = Load();
            var now = _now();
            recovered = r.RejectedSinceUtc is not null;
            r.LastOkUtc = now;
            r.RejectedSinceUtc = null;
            if (recovered || now - _lastWriteUtc >= OkWriteInterval) Write(r);
        }
        if (!recovered) return;
        Log.Info("GitHub accepts the saved sign-in again.");
        Recovered?.Invoke();
    }

    /// <summary>A call got a 401. Only the first one of a streak logs and raises <see cref="Rejected"/>.</summary>
    public void Reject(string? requestId)
    {
        string line;
        lock (_gate)
        {
            var r = Load();
            var id = CleanId(requestId);
            if (id is not null) r.LastRejectedRequestId = id;
            if (r.RejectedSinceUtc is not null) return; // same streak; the newer id stays in memory until the next write
            r.RejectedSinceUtc = _now();
            Write(r);
            line = $"GitHub sign-in rejected (request id {r.LastRejectedRequestId ?? "unknown"}): {Lifetime(r)}";
        }
        Log.Warn(line);
        StreakStarted?.Invoke();
        Rejected?.Invoke();
    }

    /// <summary>One line for the log, bug reports and `github status`.</summary>
    public string Summary()
    {
        SignInRecord r;
        lock (_gate) r = Load();
        if (!_hasToken()) return "not signed in";
        if (r.SavedUtc is null && r.LastOkUtc is null && r.RejectedSinceUtc is null) return "unknown (signed in before this was recorded)";
        lock (_gate)
            return r.RejectedSinceUtc is { } since
                ? $"rejected since {Local(since)} (request id {r.LastRejectedRequestId ?? "unknown"}): {Lifetime(r)}"
                : $"accepted: {Lifetime(r)}";
    }

    // ------------------------------------------------------------ plumbing

    private static string Lifetime(SignInRecord r) =>
        $"{(r.SavedUtc is { } s ? $"saved {Local(s)} via {r.SavedVia ?? "unknown"}" : "save time not recorded")}, " +
        $"{(r.LastOkUtc is { } ok ? $"last worked {Local(ok)}" : "never confirmed working")}";

    private static string Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Request ids look like "A1B2:3C4D:..."; anything else (or anything long) is dropped rather than stored.</summary>
    private static string? CleanId(string? id)
    {
        id = id?.Trim();
        return !string.IsNullOrEmpty(id) && id.Length <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_') ? id : null;
    }

    private static bool SavedTokenExists()
    {
        try { return GitHubAuth.LoadToken() is not null; }
        catch (Exception) { return false; }
    }

    private SignInRecord Load()
    {
        if (_rec is not null) return _rec;
        try
        {
            if (File.Exists(_file)) _rec = JsonSerializer.Deserialize<SignInRecord>(File.ReadAllText(_file), Opts);
        }
        catch (Exception ex)
        {
            Log.Warn($"Ignoring unreadable {_file}: {ex.Message}");
        }
        return _rec ??= new SignInRecord();
    }

    private void Write(SignInRecord r)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = $"{_file}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, Opts), new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
            _lastWriteUtc = _now();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save {_file}: {ex.Message}");
        }
    }
}
