using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OptimusVibe.Feedback;
using OptimusVibe.GameLab;

namespace OptimusVibe.Outbox;

public enum OutboxKind { BugReport, LabRequest }

/// <summary>
/// The stored form of a bug report. NEVER holds a token: <see cref="AsSignedInUser"/> only says "use the signed-in token if there is one
/// when it is finally sent". The draft is already sanitized.
/// </summary>
public sealed record BugReportPayload(string Title, string Body, IReadOnlyList<string> Labels, bool AsSignedInUser)
{
    public BugReportDraft ToDraft() => new(Title, Body, Labels);
}

/// <summary>
/// The stored form of a game-lab request: where to file it, the request header and the evidence parts exactly as the user confirmed
/// them (the request body is rebuilt from these by <see cref="GameLabClient.FilePreparedAsync"/>, so what is posted is what was reviewed).
/// </summary>
public sealed record LabRequestPayload(string Repo, string Branch, LabKind Kind, string Name, string Store, string StoreId, string GameId,
    string Device, string AppVersion, IReadOnlyList<string> EvidenceParts)
{
    public PreparedLabRequest ToPrepared() =>
        new(new GameLabContract.RequestInfo(Kind, GameId, Name, Store, StoreId, Device, AppVersion), EvidenceParts);
}

/// <summary>
/// One thing waiting to be sent. On disk (one file per item, see <see cref="OutboxStore"/>):
/// <c>{"v":1,"id","kind":"bugReport|labRequest","createdUtc","attempts","lastError","lastAttemptUtc","payload":{...}}</c>.
/// </summary>
public sealed record OutboxItem(string Id, OutboxKind Kind, DateTime CreatedUtc, int Attempts, string? LastError, DateTime? LastAttemptUtc, string PayloadJson)
{
    public const int Version = 1;

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static OutboxItem ForBugReport(BugReportDraft draft, bool asSignedInUser, DateTime? nowUtc = null) =>
        New(OutboxKind.BugReport, new BugReportPayload(draft.Title, draft.Body, draft.Labels.ToList(), asSignedInUser), nowUtc);

    /// <summary>A lab request with the evidence already collected (see <see cref="GameLabClient.PrepareRequest"/>).</summary>
    public static OutboxItem ForLabRequest(string repo, string branch, PreparedLabRequest request, DateTime? nowUtc = null)
    {
        var i = request.Info;
        return New(OutboxKind.LabRequest,
            new LabRequestPayload(repo, string.IsNullOrWhiteSpace(branch) ? "main" : branch, i.Kind, i.Name, i.Store, i.StoreId, i.GameId, i.Device, i.AppVersion,
                request.EvidenceParts.ToList()), nowUtc);
    }

    private static OutboxItem New<T>(OutboxKind kind, T payload, DateTime? nowUtc) =>
        new(Guid.NewGuid().ToString("N"), kind, (nowUtc ?? DateTime.UtcNow).ToUniversalTime(), 0, null, null, JsonSerializer.Serialize(payload, JsonOpts));

    public BugReportPayload BugReport =>
        Kind == OutboxKind.BugReport ? JsonSerializer.Deserialize<BugReportPayload>(PayloadJson, JsonOpts)! : throw new InvalidOperationException("Not a bug report.");

    public LabRequestPayload LabRequest =>
        Kind == OutboxKind.LabRequest ? JsonSerializer.Deserialize<LabRequestPayload>(PayloadJson, JsonOpts)! : throw new InvalidOperationException("Not a lab request.");

    /// <summary>Short text for lists: the report title, or "Game name (kind)".</summary>
    public string Summary
    {
        get
        {
            try
            {
                return Kind == OutboxKind.BugReport ? BugReport.Title : $"{LabRequest.Name} ({GameLabContract.KindText(LabRequest.Kind)})";
            }
            catch (JsonException) { return Id; }
        }
    }

    internal string ToJson()
    {
        var node = new JsonObject
        {
            ["v"] = Version,
            ["id"] = Id,
            ["kind"] = Kind == OutboxKind.BugReport ? "bugReport" : "labRequest",
            ["createdUtc"] = CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["attempts"] = Attempts,
            ["lastError"] = LastError,
            ["lastAttemptUtc"] = LastAttemptUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["payload"] = JsonNode.Parse(PayloadJson),
        };
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>Parses a stored item; throws <see cref="JsonException"/> / <see cref="InvalidOperationException"/> / <see cref="FormatException"/> when it is not valid.</summary>
    internal static OutboxItem FromJson(string json)
    {
        var n = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not an object");
        var v = n["v"]?.GetValue<int>() ?? throw new JsonException("no version");
        if (v != Version) throw new JsonException($"unsupported version {v}");
        var kind = n["kind"]?.GetValue<string>() switch
        {
            "bugReport" => OutboxKind.BugReport,
            "labRequest" => OutboxKind.LabRequest,
            var k => throw new JsonException($"unknown kind '{k}'"),
        };
        var id = n["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new JsonException("bad id");
        var payload = n["payload"] as JsonObject ?? throw new JsonException("no payload");
        var item = new OutboxItem(id, kind,
            ParseUtc(n["createdUtc"]?.GetValue<string>() ?? throw new JsonException("no createdUtc")),
            n["attempts"]?.GetValue<int>() ?? 0,
            n["lastError"]?.GetValue<string>(),
            n["lastAttemptUtc"] is { } la ? ParseUtc(la.GetValue<string>()) : null,
            payload.ToJsonString());
        if (kind == OutboxKind.BugReport) _ = item.BugReport; else _ = item.LabRequest; // validate the payload shape
        return item;
    }
}
