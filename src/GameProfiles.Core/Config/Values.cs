using System.Globalization;

namespace GameProfiles.Config;

public static class Values
{
    public static string ToScalar(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Loose equality used for diffs and drift checks: games rewrite "1" as "1.000000" or "True" as "true",
    /// which should not count as a change.
    /// </summary>
    public static bool Equivalent(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        a = a.Trim();
        b = b.Trim();
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (bool.TryParse(a, out var ba) && bool.TryParse(b, out var bb)) return ba == bb;
        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            return Math.Abs(da - db) < 1e-6;
        return false;
    }
}
