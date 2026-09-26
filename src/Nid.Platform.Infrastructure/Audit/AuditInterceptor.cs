using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary>
/// Spec §4.2: before a context saves, one log line per changed audited row is added to the same change tracker,
/// so it is written in the same batch and the same transaction — the unit of work's, or EF's own.
/// </summary>
public sealed class AuditInterceptor(IActorAccessor actors, AuditRequest request, IEnumerable<IAuditMap> maps, IClock clock)
    : SaveChangesInterceptor
{
    private readonly Dictionary<Type, AuditRule> audited = maps.SelectMany(m => m.Audited).ToDictionary(p => p.Key, p => p.Value);
    private readonly HashSet<Type> excluded = maps.SelectMany(m => m.Excluded).ToHashSet();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context!);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context!);
        return ValueTask.FromResult(result);
    }

    // A save that fails leaves its lines Added in the tracker, and a caller that catches the failure and saves again
    // (ProviderStore on a key race) would write them for a change that never happened. A saved line is Unchanged,
    // so every Added one belongs to the save that just failed.
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context!);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context!);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Discard(eventData.Context!);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context!);
        return Task.CompletedTask;
    }

    private static void Discard(DbContext context)
    {
        foreach (var line in context.ChangeTracker.Entries<AuditEntryRow>().Where(e => e.State == EntityState.Added).ToList())
            line.State = EntityState.Detached;
    }

    private void Capture(DbContext context)
    {
        context.ChangeTracker.DetectChanges();
        var at = clock.GetCurrentInstant();
        var lines = new List<AuditEntryRow>();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            var type = entry.Metadata.ClrType;
            if (excluded.Contains(type))
                continue;
            if (!audited.TryGetValue(type, out var rule))
                throw new InvalidOperationException($"{type.FullName} is neither audited nor excluded (1F3a spec §4.4).");

            var actor = actors.Current ?? throw new AuditActorMissingException(entry.Metadata.GetTableName()!);
            if (AuditDiff.Row(entry, actor, request.Name, rule.Items(entry), at) is { } line)
                lines.Add(line);
        }

        context.Set<AuditEntryRow>().AddRange(lines);
    }
}
