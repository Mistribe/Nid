using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class SecurityPriceStore(InvestmentsDbContext db) : ISecurityPriceStore
{
    public async Task<IReadOnlyDictionary<string, SecurityListing>> ListingsAsync(IReadOnlyCollection<string> isins, CancellationToken ct)
        => (await db.SecurityListings.AsNoTracking().Where(l => isins.Contains(l.Isin)).ToListAsync(ct))
            .ToDictionary(l => l.Isin, l => new SecurityListing(l.Isin, l.BoursoramaPath, l.YahooSymbol, l.ResolvedAt, l.NotFoundAt));

    public async Task SaveListingAsync(SecurityListing listing, CancellationToken ct)
    {
        var row = await db.SecurityListings.SingleOrDefaultAsync(l => l.Isin == listing.Isin, ct);
        if (row is null)
            db.SecurityListings.Add(row = new SecurityListingRow { Isin = listing.Isin });
        row.BoursoramaPath = listing.BoursoramaPath;
        row.YahooSymbol = listing.YahooSymbol;
        row.ResolvedAt = listing.ResolvedAt;
        row.NotFoundAt = listing.NotFoundAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, SecurityPrice>> LatestAcceptedAsync(IReadOnlyCollection<string> isins, CancellationToken ct)
    {
        var rows = await db.SecurityPrices.AsNoTracking().Where(p => isins.Contains(p.Isin) && p.Status == "accepted").ToListAsync(ct);
        return rows.GroupBy(p => p.Isin).ToDictionary(g => g.Key, g => ToPrice(g.MaxBy(p => p.OnDate)!));
    }

    public async Task<IReadOnlyDictionary<string, LatestPrices>> LatestAsync(IReadOnlyCollection<string> isins, CancellationToken ct)
    {
        var rows = await db.SecurityPrices.AsNoTracking().Where(p => isins.Contains(p.Isin))
            .GroupBy(p => new { p.Isin, p.Status }).Select(g => g.OrderByDescending(p => p.OnDate).First()).ToListAsync(ct);
        return rows.GroupBy(r => r.Isin).ToDictionary(g => g.Key, g =>
        {
            var accepted = g.SingleOrDefault(r => r.Status == "accepted");
            var rejected = g.SingleOrDefault(r => r.Status == "rejected");
            return new LatestPrices(accepted is null ? null : ToPrice(accepted),
                rejected is not null && (accepted is null || rejected.OnDate > accepted.OnDate) ? ToPrice(rejected) : null);
        });
    }

    public async Task<IReadOnlyList<SecurityPrice>> AcceptedOnOrBeforeAsync(IReadOnlyCollection<string> isins, LocalDate from, LocalDate to, CancellationToken ct)
        => (await db.SecurityPrices.AsNoTracking().Where(p => isins.Contains(p.Isin) && p.Status == "accepted" && p.OnDate >= from && p.OnDate <= to)
            .ToListAsync(ct)).Select(ToPrice).ToList();

    // One statement, so two writers of the same day cannot both pass a read and the second overwrite a person's acceptance.
    public async Task<bool> WriteAsync(SecurityPrice price, CancellationToken ct)
    {
        var status = price.Status == PriceStatus.Accepted ? "accepted" : "rejected";
        var acceptedBy = price.AcceptedBy?.Value;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into investments.security_price (isin, on_date, price, currency, source, status, previous, fetched_at, accepted_by)
            values ({price.Isin}, {price.OnDate}, {price.Price}, {price.Currency.Code}, {price.Source},
                {status}, {price.Previous}, {price.FetchedAt}, {acceptedBy})
            on conflict (isin, on_date) do update set price = excluded.price, currency = excluded.currency, source = excluded.source,
                status = excluded.status, previous = excluded.previous, fetched_at = excluded.fetched_at, accepted_by = excluded.accepted_by
            where security_price.accepted_by is null
            """, ct) > 0;
    }

    public async Task<bool> AcceptAsync(string isin, LocalDate onDate, PersonId by, Instant at, CancellationToken ct)
        => await db.SecurityPrices.Where(p => p.Isin == isin && p.OnDate == onDate && p.Status == "rejected")
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "accepted").SetProperty(p => p.AcceptedBy, by.Value).SetProperty(p => p.AcceptedAt, at), ct) > 0;

    public async Task<int> PurgeBeforeAsync(LocalDate before, CancellationToken ct)
        => await db.SecurityPrices.Where(p => p.OnDate < before).ExecuteDeleteAsync(ct);

    private static SecurityPrice ToPrice(SecurityPriceRow r)
        => new(r.Isin, r.OnDate, r.Price, Currency.TryCreate(r.Currency.Trim(), out var currency) ? currency : Currency.Eur, r.Source,
            r.Status == "accepted" ? PriceStatus.Accepted : PriceStatus.Rejected, r.Previous, r.FetchedAt,
            r.AcceptedBy is { } person ? new PersonId(person) : null);
}
