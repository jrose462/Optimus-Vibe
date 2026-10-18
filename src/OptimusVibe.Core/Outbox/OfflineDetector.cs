using System.Net;
using System.Net.Sockets;
using OptimusVibe.Sync;

namespace OptimusVibe.Outbox;

/// <summary>Decides whether a failed send means "no connection right now" (queue it) or a real refusal (report it).</summary>
public static class OfflineDetector
{
    /// <summary>
    /// True for failures that say GitHub could not be reached: <see cref="HttpRequestException"/> with no HTTP status (socket / DNS /
    /// TLS), a <see cref="SocketException"/>, an <see cref="IOException"/> caused by the network stack, HTTP timeouts
    /// (<see cref="TaskCanceledException"/> / <see cref="TimeoutException"/>), and <see cref="GitHubApiException"/> with status
    /// 502/503/504 or with no status whose inner exception is one of those. False for 4xx (401/403/404/422...), other
    /// exceptions, and when <paramref name="ct"/> was cancelled by the caller (the user cancelled; that is not "offline").
    /// </summary>
    public static bool IsOffline(Exception ex, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return false;
        return Check(ex, 0);
    }

    private static bool IsGatewayStatus(HttpStatusCode code) =>
        code is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool Check(Exception? ex, int depth)
    {
        if (ex is null || depth > 8) return false;
        switch (ex)
        {
            case AggregateException agg:
                return agg.InnerExceptions.Count > 0 && agg.InnerExceptions.All(e => Check(e, depth + 1));
            case GitHubApiException api:
                if (api.Status is { } status) return IsGatewayStatus(status);
                return Check(api.InnerException, depth + 1);
            case HttpRequestException http:
                if (http.StatusCode is { } code) return IsGatewayStatus(code);
                return true;
            case SocketException:
            case TimeoutException:
            case TaskCanceledException:
                return true;
            case IOException io:
                return HasNetworkCause(io, depth);
            default:
                // Wrappers (GitHubException, ConfigException...) only count when they wrap a network failure.
                return ex.InnerException is not null && Check(ex.InnerException, depth + 1);
        }
    }

    private static bool HasNetworkCause(Exception ex, int depth)
    {
        for (var e = ex.InnerException; e is not null && depth < 8; e = e.InnerException, depth++)
            if (e is SocketException or HttpRequestException or TimeoutException) return true;
        return false;
    }
}
