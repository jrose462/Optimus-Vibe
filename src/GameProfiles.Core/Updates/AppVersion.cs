using System.Reflection;
using System.Text.RegularExpressions;

namespace GameProfiles.Updates;

/// <summary>
/// The fixed APP repo: releases of the exes are published here and bug reports are filed here. Not to be confused with
/// the per-user CONFIG repo (<see cref="Config.AppSettings.GitHubRepo"/>), which only holds config/.
/// </summary>
public static class AppRepo
{
    public const string Owner = "jrose462";
    public const string Name = "GameProfiles";
    public const string FullName = Owner + "/" + Name;

    /// <summary>Release asset of the WPF app.</summary>
    public const string AppAsset = "GameProfilesApp.exe";

    /// <summary>Release asset of the command-line exe.</summary>
    public const string CliAsset = "gameprofiles.exe";

    public const string ReleasesUrl = "https://github.com/" + FullName + "/releases";
}

/// <summary>Version of the running build (Directory.Build.props; CI passes -p:Version=X.Y.Z).</summary>
public static partial class AppVersion
{
    private static readonly Lazy<string> CurrentLazy = new(Read);

    /// <summary>"X.Y.Z" (or "X.Y.Z-suffix"), from the entry assembly's informational version with any "+sha" removed.</summary>
    public static string Current => CurrentLazy.Value;

    private static string Read()
    {
        foreach (var asm in new[] { Assembly.GetEntryAssembly(), typeof(AppVersion).Assembly })
        {
            var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info) && TryParse(info, out _)) return StripBuild(info);
        }
        var v = typeof(AppVersion).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
    }

    /// <summary>"1.2.3+abc" → "1.2.3"; also trims a leading "v".</summary>
    public static string StripBuild(string version)
    {
        var s = version.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        return plus >= 0 ? s[..plus] : s;
    }

    /// <summary>Parses "v1.2.3", "1.2", "1.2.3-beta.1", "1.2.3+sha". Missing parts are 0.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = SemVerRegex().Match(text.Trim());
        if (!m.Success) return false;
        version = new SemVer(
            int.Parse(m.Groups[1].Value),
            m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0,
            m.Groups[4].Success ? m.Groups[4].Value : "");
        return true;
    }

    /// <summary>Semver ordering (pre-release &lt; release). Unparseable versions sort lowest.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var va);
        var okB = TryParse(b, out var vb);
        if (!okA || !okB) return okA.CompareTo(okB);
        return va.CompareTo(vb);
    }

    /// <summary>True when <paramref name="candidate"/> (e.g. a release tag "v1.2.3") is newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    [GeneratedRegex(@"^[vV]?(\d{1,9})(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?(?:\.\d{1,9})?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]*)?$")]
    private static partial Regex SemVerRegex();
}

/// <summary>Major.Minor.Patch[-PreRelease] with semver precedence.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string PreRelease) : IComparable<SemVer>
{
    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        var pa = PreRelease ?? "";
        var pb = other.PreRelease ?? "";
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1; // release > its pre-releases
        if (pb.Length == 0) return -1;
        var xs = pa.Split('.');
        var ys = pb.Split('.');
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            var xNum = int.TryParse(xs[i], out var xi);
            var yNum = int.TryParse(ys[i], out var yi);
            c = (xNum, yNum) switch
            {
                (true, true) => xi.CompareTo(yi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(xs[i], ys[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return xs.Length.CompareTo(ys.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(string.IsNullOrEmpty(PreRelease) ? "" : "-" + PreRelease)}";
}
