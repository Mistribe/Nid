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

    public async Task<bool> WriteAsync(SecurityPrice price, CancellationToken ct)
    {
        var row = await db.SecurityPrices.SingleOrDefaultAsync(p => p.Isin == price.Isin && p.OnDate == price.OnDate, ct);
        if (row is { AcceptedBy: not null })
            return false;
        if (row is null)
            db.SecurityPrices.Add(row = new SecurityPriceRow { Isin = price.Isin, OnDate = price.OnDate });
        row.Price = price.Price;
        row.Currency = price.Currency.Code;
        row.Source = price.Source;
        row.Status = price.Status == PriceStatus.Accepted ? "accepted" : "rejected";
        row.Previous = price.Previous;
        row.FetchedAt = price.FetchedAt;
        row.AcceptedBy = price.AcceptedBy?.Value;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> PurgeBeforeAsync(LocalDate before, CancellationToken ct)
        => await db.SecurityPrices.Where(p => p.OnDate < before).ExecuteDeleteAsync(ct);

    private static SecurityPrice ToPrice(SecurityPriceRow r)
        => new(r.Isin, r.OnDate, r.Price, Currency.TryCreate(r.Currency.Trim(), out var currency) ? currency : Currency.Eur, r.Source,
            r.Status == "accepted" ? PriceStatus.Accepted : PriceStatus.Rejected, r.Previous, r.FetchedAt,
            r.AcceptedBy is { } person ? new PersonId(person) : null);
}
