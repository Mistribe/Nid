using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Platform.Notifications;
using NodaTime;

namespace Nid.Platform.Infrastructure.Notifications;

/// <summary>The outbox of notifications design §5.1 on <see cref="PlatformDbContext"/>.</summary>
public sealed class DeliveryStore(PlatformDbContext db) : IDeliveryStore, IDigestStore
{
    public async Task EnqueueAsync(IReadOnlyList<NewDelivery> deliveries, Instant now, CancellationToken ct)
    {
        // In SQL: a delivery another run queued meanwhile makes the insert a no-op, not a failure.
        foreach (var d in deliveries)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform.notification_delivery
                    (id, notification_id, person_id, channel, round, state, attempts, next_attempt_at, created_at)
                VALUES
                    ({Guid.CreateVersion7()}, {d.NotificationId}, {d.Person.Value}, {d.Channel}, {d.Round}, {DeliveryStates.Pending}, 0, {now}, {now})
                ON CONFLICT (notification_id, channel, round) DO NOTHING
                """, ct);
        }
    }

    public async Task EnqueueHouseholdAsync(IReadOnlyList<NewHouseholdDelivery> deliveries, Instant now, CancellationToken ct)
    {
        foreach (var d in deliveries)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform.notification_delivery
                    (id, notification_id, person_id, channel, round, state, attempts, next_attempt_at, created_at, household_key)
                VALUES
                    ({Guid.CreateVersion7()}, {d.NotificationId}, {d.Person.Value}, {d.Channel}, {d.Round}, {DeliveryStates.Pending}, 0, {now}, {now}, {d.Key})
                ON CONFLICT (household_key) WHERE household_key IS NOT NULL DO NOTHING
                """, ct);
        }
    }

    public async Task<IReadOnlyList<DeliveryRecord>> DueAsync(Instant now, int limit, CancellationToken ct)
        => await db.Deliveries.AsNoTracking()
            .Where(d => d.State == DeliveryStates.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).ThenBy(d => d.Id)
            .Take(limit)
            .Select(d => new DeliveryRecord(d.Id, d.NotificationId, new PersonId(d.PersonId), d.Channel, d.Round, d.Attempts, d.DigestId, d.HouseholdKey))
            .ToListAsync(ct);

    public async Task<DeliveryRecord?> ClaimAsync(Guid id, Instant now, CancellationToken ct)
        => await db.Deliveries.AsNoTracking()
            .Where(d => d.Id == id && d.State == DeliveryStates.Pending && d.NextAttemptAt <= now)
            .Select(d => new DeliveryRecord(d.Id, d.NotificationId, new PersonId(d.PersonId), d.Channel, d.Round, d.Attempts, d.DigestId, d.HouseholdKey))
            .FirstOrDefaultAsync(ct);

    public async Task<(IReadOnlyList<FailedDelivery> Items, int Total)> FailedSincePageAsync(Instant since, int skip, int take, CancellationToken ct)
    {
        // A left join: the notification may have been purged, the failure stays listed.
        var failed = from d in db.Deliveries.AsNoTracking()
                     where d.State == DeliveryStates.Failed && d.CreatedAt >= since
                     join n in db.Notifications.AsNoTracking() on d.NotificationId equals (Guid?)n.Id into rows
                     from n in rows.DefaultIfEmpty()
                     orderby d.CreatedAt descending, d.Id
                     select new FailedDelivery(d.Id, d.Channel, n == null ? (d.DigestId != null ? "digest" : "") : n.Kind, d.CreatedAt, d.LastError ?? "");
        return (await failed.Skip(skip).Take(take).ToListAsync(ct), await failed.CountAsync(ct));
    }

    public async Task SettleAsync(Guid id, string state, int attempts, Instant? nextAttemptAt, string? lastError, Instant? sentAt, CancellationToken ct)
    {
        var row = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == id && d.State == DeliveryStates.Pending, ct);
        if (row is null)
            return;

        row.State = state;
        row.Attempts = attempts;
        row.NextAttemptAt = nextAttemptAt;
        row.LastError = lastError;
        row.SentAt = sentAt;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached; // the next delivery of the batch starts clean
        }
    }

    public async Task DeferAsync(Guid id, Instant nextAttemptAt, CancellationToken ct)
        => await db.Deliveries.Where(d => d.Id == id && d.State == DeliveryStates.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, nextAttemptAt), ct);

    public async Task<bool> TryCreateAsync(Guid id, PersonId person, string periodKey, Instant now, CancellationToken ct)
        // In SQL: the unique (person, period) makes the second insert a no-op; only the run that inserted goes on.
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.notification_digest (id, person_id, period_key, created_at)
            VALUES ({id}, {person.Value}, {periodKey}, {now})
            ON CONFLICT (person_id, period_key) DO NOTHING
            """, ct) == 1;

    public async Task<IReadOnlyList<NotificationRecord>> CandidatesAsync(PersonId person, CancellationToken ct)
        => (await db.Notifications.AsNoTracking()
                .Where(n => n.PersonId == person.Value && n.State == NotificationStates.Open && n.DigestedAt == null
                            // Not already sent on its own for this round (a skipped message sent nothing).
                            && !db.Deliveries.Any(d => d.NotificationId == n.Id && d.Round == n.Round && d.State != DeliveryStates.Skipped))
                .OrderBy(n => n.OpenedAt).ThenBy(n => n.Id)
                .ToListAsync(ct))
            .Select(NotificationStore.ToRecord).ToList();

    public async Task<int> AttachAsync(Guid digestId, PersonId person, IReadOnlyCollection<Guid> rows, Instant now, CancellationToken ct)
    {
        var wanted = rows.ToArray();
        return await db.Notifications
            .Where(n => n.PersonId == person.Value && wanted.Contains(n.Id) && n.State == NotificationStates.Open && n.DigestedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.DigestedAt, now).SetProperty(n => n.DigestId, digestId), ct);
    }

    public async Task EnqueueAsync(IReadOnlyList<NewDigestDelivery> deliveries, Instant now, CancellationToken ct)
    {
        foreach (var d in deliveries)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO platform.notification_delivery
                    (id, digest_id, person_id, channel, round, state, attempts, next_attempt_at, created_at)
                VALUES
                    ({Guid.CreateVersion7()}, {d.DigestId}, {d.Person.Value}, {d.Channel}, 1, {DeliveryStates.Pending}, 0, {now}, {now})
                ON CONFLICT (digest_id, channel) DO NOTHING
                """, ct);
        }
    }

    public async Task<IReadOnlyList<NotificationRecord>> OpenRowsAsync(Guid digestId, CancellationToken ct)
        => (await db.Notifications.AsNoTracking()
                .Where(n => n.DigestId == digestId && n.State == NotificationStates.Open)
                .OrderBy(n => n.OpenedAt).ThenBy(n => n.Id)
                .ToListAsync(ct))
            .Select(NotificationStore.ToRecord).ToList();

    public async Task ReleaseAsync(Guid digestId, CancellationToken ct)
        => await db.Notifications
            .Where(n => n.DigestId == digestId && n.State == NotificationStates.Open
                        && !db.Deliveries.Any(d => d.DigestId == digestId && (d.State == DeliveryStates.Sent || d.State == DeliveryStates.Pending)))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.DigestedAt, (Instant?)null).SetProperty(n => n.DigestId, (Guid?)null), ct);
}
