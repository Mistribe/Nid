using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Platform.Audit;

/// <summary>One line of the log as Platform reads it.</summary>
public sealed record AuditRecord(
    long Seq, Instant At, string Category, string Action, string ActorKind, Guid? PersonId, string Via, string? Request,
    string? Entity, string? EntityKey, IReadOnlyList<Guid> ItemIds, Guid? SubjectPersonId, string Data, Guid? TokenId);

public interface IAuditLog
{
    /// <summary>Write entries under <paramref name="item"/> whose items are all in <paramref name="visible"/> (spec U9), newest first.</summary>
    Task<IReadOnlyList<AuditRecord>> ItemHistoryAsync(ItemId item, IReadOnlySet<ItemId> visible, long? before, int limit, CancellationToken ct);

    /// <summary>
    /// Security entries, newest first: about <paramref name="subject"/> when given; restricted to <paramref name="events"/>
    /// when given; <paramref name="withoutSelfRevocations"/> drops the <c>session_revoked</c> an adult made on themselves.
    /// </summary>
    Task<IReadOnlyList<AuditRecord>> SecurityEventsAsync(
        PersonId? subject, IReadOnlyCollection<string>? events, bool withoutSelfRevocations, long? before, int limit, CancellationToken ct);

    /// <summary>
    /// One page of the admin journal: <paramref name="events"/> only, the <c>session_revoked</c> an adult made on themselves left out,
    /// newest first (<c>Seq</c> descending), with the total of the same filter.
    /// </summary>
    Task<(IReadOnlyList<AuditRecord> Items, int Total)> SecurityEventsPageAsync(
        IReadOnlyCollection<string>? events, int skip, int take, CancellationToken ct);
}

public static class AuditPaging
{
    public static int Limit(int? requested) => Math.Clamp(requested ?? 50, 1, 200);

    public static long? Next(IReadOnlyList<AuditRecord> page, int limit) => page.Count == limit ? page[^1].Seq : null;
}
