using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Audit;

public sealed record AuditActorView(string Kind, Guid? PersonId, string Via, Guid? TokenId);

public sealed record ItemHistoryEntry(
    long Seq, DateTimeOffset At, string Action, string Entity, string EntityKey, AuditActorView Actor, string? Request,
    JsonElement? Before, JsonElement? After, IReadOnlyList<Guid> ItemIds);

/// <summary>
/// Carries item ids, so the output guard checks every one (spec §6). Its before/after are raw columns: a <c>document_id</c>
/// (a stamped value or flow, a proposal, a document link) goes out only to a reader of that document (issue #175). A
/// document link's composite key (<c>EntityKey</c>, a JSON object) names its document too.
/// </summary>
public sealed record ItemHistoryPage(IReadOnlyList<ItemHistoryEntry> Entries, long? Next) : IExposesItems, IExposesDocuments
{
    private const string DocumentColumn = "document_id";

    public IEnumerable<ItemId> ExposedItems() => Entries.SelectMany(e => e.ItemIds).Distinct().Select(i => new ItemId(i));

    public IEnumerable<Guid> ExposedDocuments()
        => Entries.SelectMany(e => new[] { DocumentIn(e.Before), DocumentIn(e.After), DocumentIn(Key(e.EntityKey)) }).OfType<Guid>().Distinct();

    public object WithReadableDocuments(IReadOnlySet<Guid> readable) => this with
    {
        Entries = Entries.Select(e => e with
        {
            Before = Masked(e.Before, readable), After = Masked(e.After, readable),
            EntityKey = DocumentIn(Key(e.EntityKey)) is { } id && !readable.Contains(id) ? Masked(Key(e.EntityKey), readable)!.Value.GetRawText() : e.EntityKey,
        }).ToList(),
    };

    /// <summary>A composite key is a JSON object; a single key is the bare value.</summary>
    private static JsonElement? Key(string entityKey)
    {
        if (!entityKey.StartsWith('{'))
            return null;
        try
        {
            using var key = JsonDocument.Parse(entityKey);
            return key.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null; // keys are written by AuditDiff; a broken one must not withhold the whole history
        }
    }

    private static Guid? DocumentIn(JsonElement? side)
        => side is { ValueKind: JsonValueKind.Object } row && row.TryGetProperty(DocumentColumn, out var value)
            && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static JsonElement? Masked(JsonElement? side, IReadOnlySet<Guid> readable)
    {
        if (DocumentIn(side) is not { } id || readable.Contains(id))
            return side;
        var row = JsonNode.Parse(side!.Value.GetRawText())!.AsObject();
        row[DocumentColumn] = null;
        using var masked = JsonDocument.Parse(row.ToJsonString());
        return masked.RootElement.Clone();
    }
}

/// <summary>An item a withheld response carried; <paramref name="Kind"/> and <paramref name="Name"/> only when the reading admin may see it.</summary>
public sealed record SecurityEventItem(Guid Id, string? Kind, string? Name);

/// <summary>
/// <see cref="Details"/> holds only what each event may show. <see cref="Items"/> is filled by the admin journal alone, for a response
/// withheld for items outside the visible set (#28); no other view carries an item id (spec §5.1).
/// </summary>
public sealed record SecurityEventView(
    long Seq, DateTimeOffset At, string Event, AuditActorView Actor, Guid? SubjectPersonId, IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<SecurityEventItem> Items);

public sealed record SecurityEventPage(IReadOnlyList<SecurityEventView> Entries, long? Next);

public sealed record ItemHistoryQuery(ItemId ItemId, long? Before, int? Limit) : IQuery<ItemHistoryPage>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

public sealed record MySecurityEventsQuery(long? Before, int? Limit) : IQuery<SecurityEventPage>;

public static partial class SecurityEventViews
{
    /// <summary>The details each event may show. Everything else in <c>data</c> stays in the table for the host.</summary>
    private static readonly Dictionary<string, string[]> Shown = new()
    {
        [SecurityEventNames.LoginRefused] = ["subject", "reason"],
        [SecurityEventNames.AdminGranted] = ["source"],
        [SecurityEventNames.AdminRevoked] = ["source"],
        [SecurityEventNames.AccessDenied] = ["access"],
        [SecurityEventNames.AccessViolation] = ["request", "reason", "count", "error"],
        [SecurityEventNames.HouseholdSettingsChanged] = ["fields"],
        [SecurityEventNames.TokenCreated] = ["preset"],
        [SecurityEventNames.TokenRevoked] = ["count"],
        [SecurityEventNames.TokenAuthFailed] = ["prefix"],
        [SecurityEventNames.McpToolCalled] = ["tool"],
        [SecurityEventNames.BankStatementImported] = ["account", "source", "new", "known", "probably_known", "kept"],
    };

    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    private sealed partial class AuditDataJson : JsonSerializerContext;

    public static SecurityEventView From(AuditRecord r)
    {
        // Minor #7: `data` is written with string values only today, but the view must not 500 the journal if
        // some future event's field is not a string — that value is simply left out, not surfaced garbled.
        var data = JsonSerializer.Deserialize(r.Data, AuditDataJson.Default.DictionaryStringJsonElement) ?? [];
        var details = Shown.GetValueOrDefault(r.Action, [])
            .Where(k => data.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String)
            .ToDictionary(k => k, k => data[k].GetString()!);
        return new SecurityEventView(r.Seq, r.At.ToDateTimeOffset(), r.Action, new AuditActorView(r.ActorKind, r.PersonId, r.Via, r.TokenId), r.SubjectPersonId, details, []);
    }

    public static SecurityEventPage Page(IReadOnlyList<AuditRecord> records, int limit)
        => new(records.Select(From).ToList(), AuditPaging.Next(records, limit));
}

[RequestHandler<ItemHistoryQuery, ItemHistoryPage>]
public sealed class ItemHistoryHandler(IAuditLog log, IVisibleItems visible, IActorAccessor accessor)
    : IRequestHandler<ItemHistoryQuery, ItemHistoryPage>
{
    public async ValueTask<Result<ItemHistoryPage>> HandleAsync(ItemHistoryQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<ItemHistoryPage>(new UnauthorizedFailure("An item's history is read by a person.", null));

        var limit = AuditPaging.Limit(query.Limit);
        var records = await log.ItemHistoryAsync(query.ItemId, await visible.ForAsync(me, VisibilityScope.Me, includeArchived: true, ct), query.Before, limit, ct);
        return Result.Success(new ItemHistoryPage(records.Select(ToEntry).ToList(), AuditPaging.Next(records, limit)));
    }

    private static ItemHistoryEntry ToEntry(AuditRecord r)
    {
        using var data = JsonDocument.Parse(r.Data);
        JsonElement? Side(string name) => data.RootElement.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
        return new ItemHistoryEntry(r.Seq, r.At.ToDateTimeOffset(), r.Action, r.Entity!, r.EntityKey!,
            new AuditActorView(r.ActorKind, r.PersonId, r.Via, r.TokenId), r.Request, Side("before"), Side("after"), r.ItemIds);
    }
}

[RequestHandler<MySecurityEventsQuery, SecurityEventPage>]
public sealed class MySecurityEventsHandler(IAuditLog log, IActorAccessor accessor) : IRequestHandler<MySecurityEventsQuery, SecurityEventPage>
{
    public async ValueTask<Result<SecurityEventPage>> HandleAsync(MySecurityEventsQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<SecurityEventPage>(new UnauthorizedFailure("Only a person has security events.", null));

        var limit = AuditPaging.Limit(query.Limit);
        return Result.Success(SecurityEventViews.Page(await log.SecurityEventsAsync(me, null, false, query.Before, limit, ct), limit));
    }
}
