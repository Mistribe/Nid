using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using Npgsql;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>Providers and labels as rows. It decides nothing: visibility is <c>ProviderAccess</c>'s, merging the domain's.</summary>
public sealed class ProviderStore(SubscriptionsDbContext db) : IProviderStore
{
    private const string PrimaryKey = "pk_provider";
    private const string LikeEscape = "\\";

    public async Task<IReadOnlyList<Label>> LabelsAsync(CancellationToken ct)
        => (await db.Labels.AsNoTracking().OrderBy(l => l.Key).ToListAsync(ct)).Select(ToLabel).ToList();

    public async Task<IReadOnlyDictionary<string, Provider>> FindAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return new Dictionary<string, Provider>();

        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Providers.AsNoTracking().Where(p => wanted.Contains(p.Key)).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, ToProvider, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<Provider>> VisibleAsync(
        PersonId actor, IReadOnlyCollection<ItemId> visibleItems, string? nameContains, CancellationToken ct)
    {
        var query = Visible(actor, visibleItems);
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            var pattern = "%" + EscapeLike(nameContains.Trim()) + "%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern, LikeEscape));
        }

        var rows = await query.OrderBy(p => p.Name).ThenBy(p => p.Key).ToListAsync(ct);
        return rows.Select(ToProvider).ToList();
    }

    public async Task<IReadOnlyDictionary<string, Provider>> FindVisibleAsync(
        PersonId actor, IReadOnlyCollection<ItemId> visibleItems, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return new Dictionary<string, Provider>();

        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();
        var rows = await Visible(actor, visibleItems).Where(p => wanted.Contains(p.Key)).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, ToProvider, StringComparer.Ordinal);
    }

    /// <summary>
    /// The one place the rule of design §3.5 is written as a query. The creator clause ("while no subscription visible
    /// to them uses it") complements the second one: together they make a custom provider always visible to its creator,
    /// which reveals nothing (the creator knows it exists, and sees no subscription they could not see before). Written
    /// as the spec reads anyway, so a later change of either clause stays a change of one line.
    /// </summary>
    private IQueryable<ProviderRow> Visible(PersonId actor, IReadOnlyCollection<ItemId> visibleItems)
    {
        var items = visibleItems.Select(i => i.Value).ToList();
        var person = actor.Value;
        var usedVisibly = db.Subscriptions.Where(s => s.ProviderKey != null && items.Contains(s.ItemId)).Select(s => s.ProviderKey);
        return db.Providers.AsNoTracking().Where(p =>
            p.Origin == "catalog"
            || usedVisibly.Contains(p.Key)
            || (p.Origin == "custom" && p.CreatedBy == person && !usedVisibly.Contains(p.Key)));
    }

    /// <summary>A search is taken literally: <c>%</c>, <c>_</c> and the escape character itself match only themselves.</summary>
    private static string EscapeLike(string text)
        => text.Replace(LikeEscape, LikeEscape + LikeEscape).Replace("%", LikeEscape + "%").Replace("_", LikeEscape + "_");

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => db.Providers.AnyAsync(p => p.Key == key, ct);

    public async Task<Result> CreateAsync(Provider provider, CancellationToken ct)
    {
        var row = ToRow(provider);
        db.Providers.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: PrimaryKey })
        {
            db.Entry(row).State = EntityState.Detached;
            return Result.Failure(new Failure(CatalogCodes.KeyTaken, $"The key '{provider.Key}' is taken.", null));
        }

        db.Entry(row).State = EntityState.Detached;
        return Result.Success();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ItemId>>> UsageAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
            return new Dictionary<string, IReadOnlyList<ItemId>>(StringComparer.Ordinal);

        var links = await db.Subscriptions.AsNoTracking()
            .Where(s => s.ProviderKey != null && wanted.Contains(s.ProviderKey))
            .Select(s => new { Key = s.ProviderKey!, s.ItemId })
            .ToListAsync(ct);
        var byKey = links.ToLookup(l => l.Key, l => l.ItemId, StringComparer.Ordinal);
        return wanted.ToDictionary(
            key => key,
            key => (IReadOnlyList<ItemId>)byKey[key].Order().Select(id => new ItemId(id)).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<CatalogSnapshot> LockForSyncAsync(CancellationToken ct)
    {
        RequireTransaction();
        // Review Focus 5: waits for any transaction writing providers or labels (an import committing a custom provider)
        // and holds them off until this one ends. Subscriptions keep linking meanwhile: a foreign-key check takes
        // ROW SHARE, which SHARE ROW EXCLUSIVE admits.
        await db.Database.ExecuteSqlRawAsync("lock table subscriptions.provider, subscriptions.label in share row exclusive mode", ct);
        var providers = await db.Providers.AsNoTracking().ToListAsync(ct);
        var labels = await db.Labels.AsNoTracking().ToListAsync(ct);
        return new CatalogSnapshot(providers.Select(ToProvider).ToList(), labels.Select(ToLabel).ToList());
    }

    public async Task WriteCatalogAsync(CatalogPlan plan, Instant now, CancellationToken ct)
    {
        RequireTransaction();

        var labelKeys = plan.Labels.Select(l => l.Key).ToList();
        var labelRows = await db.Labels.Where(l => labelKeys.Contains(l.Key)).ToDictionaryAsync(l => l.Key, ct);
        foreach (var label in plan.Labels)
        {
            if (labelRows.TryGetValue(label.Key, out var row))
            {
                if (row.Origin == "local")
                    continue; // the plan never holds one: a local label is never touched
                row.Name = label.Name;
                row.Color = label.Color;
            }
            else
                db.Labels.Add(new LabelRow { Key = label.Key, Name = label.Name, Color = label.Color, Origin = label.Origin.ToWire() });
        }

        var providerKeys = plan.Providers.Select(p => p.Key).ToList();
        var providerRows = await db.Providers.Where(p => providerKeys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, ct);
        foreach (var provider in plan.Providers)
        {
            if (providerRows.TryGetValue(provider.Key, out var row))
            {
                if (row.Origin == "custom")
                    continue; // the plan never holds one: a custom provider is never overwritten
                Apply(row, provider);
            }
            else
                db.Providers.Add(ToRow(provider));
        }

        await db.SaveChangesAsync(ct);

        var archive = plan.Archive.ToList();
        if (archive.Count > 0)
            await db.Providers
                .Where(p => archive.Contains(p.Key) && p.Origin == "catalog" && p.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (Instant?)now), ct);

        db.ChangeTracker.Clear();
    }

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A catalog sync runs inside a transaction (IUnitOfWork).");
    }

    internal static Label ToLabel(LabelRow row) => new(row.Key, row.Name, row.Color, CatalogNames.LabelOriginFromWire(row.Origin));

    internal static Provider ToProvider(ProviderRow row) => new(
        row.Key,
        CatalogNames.ProviderOriginFromWire(row.Origin),
        row.Name,
        row.Website,
        row.PricingPage,
        row.LabelKeys.ToList(),
        row.CatalogCommit,
        row.CreatedBy is { } person ? new PersonId(person) : null,
        row.ArchivedAt,
        row.ImportRef is { } importRef ? new ImportRef(importRef, row.ImportHash!) : null);

    internal static ProviderRow ToRow(Provider provider)
    {
        var row = new ProviderRow { Key = provider.Key };
        Apply(row, provider);
        return row;
    }

    /// <summary>Every column but the key and <c>xmin</c>, from the provider as it must be stored.</summary>
    internal static void Apply(ProviderRow row, Provider provider)
    {
        row.Origin = provider.Origin.ToWire();
        row.Name = provider.Name;
        row.Website = provider.Website;
        row.PricingPage = provider.PricingPage;
        row.LabelKeys = provider.LabelKeys.ToList();
        row.CatalogCommit = provider.CatalogCommit;
        row.CreatedBy = provider.CreatedBy?.Value;
        row.ArchivedAt = provider.ArchivedAt;
        row.ImportRef = provider.Import?.Ref;
        row.ImportHash = provider.Import?.Hash;
    }
}
