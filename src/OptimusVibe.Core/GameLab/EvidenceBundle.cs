using System.Text;
using System.Text.RegularExpressions;
using OptimusVibe.Feedback;

namespace OptimusVibe.GameLab;

public enum EvidenceKind { Install, File, Registry, Directory, CloudCheck, CurrentConfig }

/// <summary>One <c>### Kind: title</c> block with a fenced body.</summary>
public sealed record EvidenceSection(EvidenceKind Kind, string Title, string Body)
{
    public string Heading => Kind switch
    {
        EvidenceKind.File => $"### File: {Title}",
        EvidenceKind.Registry => $"### Registry: {Title}",
        EvidenceKind.Directory => $"### Directory: {Title}",
        EvidenceKind.Install => $"### Install: {Title}",
        EvidenceKind.CloudCheck => "### Cloud check",
        _ => "### Current config",
    };

    /// <summary>Markdown: heading + fenced body (the fence is longer than any backtick run in the body).</summary>
    public string Render() => RenderWith(Heading, Body);

    internal static string RenderWith(string heading, string body)
    {
        var longest = Regex.Matches(body, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{heading}\n{fence}\n{body.TrimEnd('\n', '\r')}\n{fence}\n\n";
    }
}

/// <summary>Everything collected for one request, already tokenized and sanitized.</summary>
public sealed class EvidenceBundle
{
    public List<EvidenceSection> Sections { get; } = [];

    /// <summary>Human notes: what was skipped, why, errors. Rendered as a final "### Notes" block, and printed by the CLI.</summary>
    public List<string> Notes { get; } = [];

    public int Count(EvidenceKind kind) => Sections.Count(s => s.Kind == kind);

    /// <summary>The whole bundle as Markdown (not split).</summary>
    public string Render() => string.Concat(Blocks(int.MaxValue));

    /// <summary>
    /// Splits into comment-sized parts of at most <paramref name="maxChars"/> characters (default 60,000). Whole sections stay
    /// together; a section bigger than a part is cut at line breaks into "(part k/m)" continuation blocks. Each part
    /// starts with the lab-evidence marker (the first part may be embedded in the issue body instead, see <see cref="GameLabContract.RequestBody"/>).
    /// </summary>
    public IReadOnlyList<string> Split(int maxChars = GameLabContract.MaxPartChars)
    {
        // Leave room for the marker/header of each part.
        var budget = maxChars - GameLabContract.EvidenceHeader(99, 99).Length - 16;
        var bodies = new List<StringBuilder> { new() };
        foreach (var block in Blocks(budget))
        {
            if (bodies[^1].Length > 0 && bodies[^1].Length + block.Length > budget) bodies.Add(new StringBuilder());
            bodies[^1].Append(block);
        }
        if (bodies[^1].Length == 0) bodies.RemoveAt(bodies.Count - 1);
        if (bodies.Count == 0) bodies.Add(new StringBuilder("_No evidence could be collected on this PC._\n"));
        return bodies.Select((b, i) => GameLabContract.EvidenceHeader(i + 1, bodies.Count) + b).ToList();
    }

    /// <summary>Rendered blocks, each at most <paramref name="max"/> chars (long sections cut into continuations).</summary>
    private IEnumerable<string> Blocks(int max)
    {
        foreach (var s in Sections)
        {
            var whole = s.Render();
            if (whole.Length <= max) { yield return whole; continue; }

            // Cut the body into chunks that, rendered, fit.
            var overhead = s.Heading.Length + 64;
            var chunks = new List<string>();
            var cur = new StringBuilder();
            foreach (var line in s.Body.Split('\n'))
            {
                var l = line.Length > max / 4 ? line[..(max / 4)] + " ...[line cut]" : line;
                if (cur.Length > 0 && cur.Length + l.Length + 1 + overhead > max) { chunks.Add(cur.ToString()); cur.Clear(); }
                cur.Append(l).Append('\n');
            }
            if (cur.Length > 0) chunks.Add(cur.ToString());
            for (var i = 0; i < chunks.Count; i++)
                yield return EvidenceSection.RenderWith($"{s.Heading} (part {i + 1}/{chunks.Count})", chunks[i]);
        }
        if (Notes.Count > 0)
            yield return EvidenceSection.RenderWith("### Notes", string.Join("\n", Notes));
    }
}

/// <summary>Redaction applied to every byte of evidence before it leaves the PC.</summary>
public static partial class EvidenceSanitizer
{
    /// <summary>
    /// Known folders → tokens, then <see cref="BugReport.Sanitize"/> (user name, machine name, emails, Steam ids, IPs, tokens, other
    /// profile paths) and a value redactor for keys that look like secrets (tokens, passwords, cookies...).
    /// </summary>
    public static string Sanitize(string text, LabFolders folders, string? userProfile = null, string? userName = null, string? machineName = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = folders.TokenizeText(text);
        s = BugReport.Sanitize(s, userProfile ?? folders.Get("UserProfile"), userName, machineName);
        s = SecretKeyLine().Replace(s, "$1[REDACTED]");
        s = SecretJson().Replace(s, "$1\"[REDACTED]\"");
        return s;
    }

    // key = value / key: value on one line, where the key mentions a secret-ish word.
    [GeneratedRegex(@"(?im)^([ \t]*[""']?[\w.\- \[\]]*(?:token|passw(?:or)?d|passwd|secret|api[_-]?key|cookie|session[_-]?id|authorization|credential|private[_-]?key)[\w.\- \[\]]*[""']?[ \t]*[=:][ \t]*).+$")]
    private static partial Regex SecretKeyLine();

    // "somethingToken": "value" / "secret": 123 inside JSON that isn't line oriented.
    [GeneratedRegex(@"(?i)(""[\w.\-]*(?:token|passw(?:or)?d|secret|api[_-]?key|cookie|session[_-]?id|credential|private[_-]?key)[\w.\-]*""\s*:\s*)(?:""(?:[^""\\]|\\.)*""|[^\s,}\]]+)")]
    private static partial Regex SecretJson();
}
