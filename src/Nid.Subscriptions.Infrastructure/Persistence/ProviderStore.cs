using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Npgsql;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>Providers and labels as rows. It decides nothing: visibility is <c>ProviderAccess</c>'s, merging the domain's.</summary>
public sealed class ProviderStore(SubscriptionsDbContext db) : IProviderStore
{
    private const string PrimaryKey = "pk_provider";

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
