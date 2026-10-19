using System.Globalization;
using OptimusVibe.Config;
using OptimusVibe.Devices;

namespace OptimusVibe.Display;

/// <summary>One active (attached to the desktop) display.</summary>
public sealed record ActiveDisplay(string DeviceName, string FriendlyName, int Width, int Height, int Refresh, bool IsPrimary)
{
    public override string ToString() =>
        $"{DeviceName} '{FriendlyName}' {Width}x{Height}@{Refresh}Hz{(IsPrimary ? " (primary)" : "")}";
}

/// <summary>Outcome of <see cref="BigPictureDetector.Detect"/>.</summary>
public sealed record BigPictureResult(bool Active, string Reason, string? TargetDisplay, DisplayModeDef Mode, string Preset, bool FromStream)
{
    public static readonly DisplayModeDef DefaultMode = new() { Width = 3840, Height = 2160, Refresh = 120 };
}

/// <summary>
/// Decides whether a big TV is in use: a stream client (Apollo/Sunshine) asking for a 4K-class stream, or an active
/// display that looks like the TV. Pure (displays and environment are passed in) so it is unit tested.
/// </summary>
public static class BigPictureDetector
{
    public const string DefaultPreset = "big-picture";
    public const int DefaultMinWidth = 3840;
    public const string DefaultAspect = "16:9";

    /// <summary>Environment variables Sunshine and Apollo export to apps and prep commands (Apollo keeps both families).</summary>
    public static readonly string[] EnvNames =
    [
        "SUNSHINE_CLIENT_WIDTH", "SUNSHINE_CLIENT_HEIGHT", "SUNSHINE_CLIENT_FPS", "SUNSHINE_CLIENT_HDR",
        "APOLLO_CLIENT_WIDTH", "APOLLO_CLIENT_HEIGHT", "APOLLO_CLIENT_FPS", "APOLLO_CLIENT_HDR",
    ];

    public static string PresetName(DeviceDef? dev) =>
        string.IsNullOrWhiteSpace(dev?.BigPicture?.Preset) ? DefaultPreset : dev!.BigPicture!.Preset!.Trim();

    public static BigPictureResult Detect(DeviceDef? dev, IReadOnlyList<ActiveDisplay> displays, IReadOnlyDictionary<string, string> env)
    {
        var bp = dev?.BigPicture;
        var match = bp?.Match;
        var mode = bp?.Display ?? BigPictureResult.DefaultMode;
        var preset = PresetName(dev);
        var minWidth = match?.MinWidth ?? DefaultMinWidth;
        var stream = match?.Stream ?? true;

        BigPictureResult No(string why) => new(false, why, null, mode, preset, false);

        if (stream && StreamClient(env) is { } c && c.Width >= minWidth)
        {
            var fps = c.Fps is { } f ? $"@{f}" : "";
            return new BigPictureResult(true, $"stream client {c.Width}x{c.Height}{fps}", null, mode, preset, true);
        }

        var patterns = Patterns(match?.Name);
        var (aw, ah) = ParseAspect(match?.Aspect ?? DefaultAspect);
        var own = dev?.Display;

        foreach (var d in displays.OrderByDescending(d => d.IsPrimary))
        {
            var named = patterns.Count > 0 && patterns.Any(p => DeviceDetector.Wildcard(p, d.FriendlyName ?? ""));
            var looksLikeTv = d.Width >= minWidth && d.Height > 0 && aw > 0
                              && Math.Abs((double)d.Width / d.Height - (double)aw / ah) < 0.02
                              && !(own is { Width: { } ow, Height: { } oh } && ow == d.Width && oh == d.Height);
            if (named || looksLikeTv)
                return new BigPictureResult(true, $"TV display '{d.FriendlyName}' {d.Width}x{d.Height}", d.DeviceName, mode, preset, false);
        }
        return No(stream ? "no 4K TV display and no 4K stream client" : "no 4K TV display (stream detection is off)");
    }

    /// <summary>Detect with this machine's real displays and environment. Never throws.</summary>
    public static BigPictureResult DetectNow(DeviceDef? dev)
    {
        try
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in EnvNames)
                if (Environment.GetEnvironmentVariable(n) is { Length: > 0 } v) env[n] = v;
            return Detect(dev, DisplayManager.GetActiveDisplays(), env);
        }
        catch (Exception ex)
        {
            Logging.Log.Warn($"Big Picture detection failed: {ex.Message}");
            return new BigPictureResult(false, "detection failed: " + ex.Message, null,
                dev?.BigPicture?.Display ?? BigPictureResult.DefaultMode, PresetName(dev), false);
        }
    }

    private sealed record StreamInfo(int Width, int Height, string? Fps);

    private static StreamInfo? StreamClient(IReadOnlyDictionary<string, string> env)
    {
        string? Get(string name) =>
            env.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v : null;
        foreach (var prefix in new[] { "SUNSHINE_CLIENT_", "APOLLO_CLIENT_" })
        {
            if (!int.TryParse(Get(prefix + "WIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) || w <= 0) continue;
            int.TryParse(Get(prefix + "HEIGHT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h);
            string? fps = null;
            if (double.TryParse(Get(prefix + "FPS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
                fps = Math.Round(f).ToString(CultureInfo.InvariantCulture);
            return new StreamInfo(w, h, fps);
        }
        return null;
    }

    private static List<string> Patterns(object? name) => name switch
    {
        null => [],
        string s => string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()],
        System.Collections.IEnumerable list => list.Cast<object?>().Select(o => o?.ToString()?.Trim() ?? "").Where(s => s.Length > 0).ToList(),
        _ => [],
    };

    private static (int W, int H) ParseAspect(string s)
    {
        var p = s.Split(':', 'x', '/');
        return p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) && a > 0 && b > 0 ? (a, b) : (16, 9);
    }
}
