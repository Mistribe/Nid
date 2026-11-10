using Microsoft.EntityFrameworkCore;
using Nid.RealEstate.Domain;

namespace Nid.RealEstate.Infrastructure.Persistence;

public sealed class IrlStore(RealEstateDbContext db) : IIrlStore
{
    public async Task<IReadOnlyList<IrlValue>> ListAsync(CancellationToken ct)
        => (await db.Irl.AsNoTracking().OrderBy(r => r.Quarter).ToListAsync(ct))
            .Select(r => new IrlValue(IrlQuarter.Parse(r.Quarter), r.Value, r.PublishedOn, r.FetchedOn, r.Source)).ToList();

    public async Task<(IReadOnlyList<IrlValue> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct)
    {
        var rows = await db.Irl.AsNoTracking().OrderByDescending(r => r.Quarter).Skip(skip).Take(take).ToListAsync(ct);
        return (rows.Select(r => new IrlValue(IrlQuarter.Parse(r.Quarter), r.Value, r.PublishedOn, r.FetchedOn, r.Source)).ToList(), await db.Irl.CountAsync(ct));
    }

    public async Task<bool> UpsertManualAsync(IrlValue value, CancellationToken ct)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into realestate.irl (quarter, value, published_on, fetched_on, source)
            values ({value.Quarter.ToString()}, {value.Value}, {value.PublishedOn}, {value.FetchedOn}, 'manual')
            on conflict (quarter) do update
              set value = excluded.value, published_on = excluded.published_on, fetched_on = excluded.fetched_on, source = 'manual'
              where realestate.irl.source <> 'insee'
            """, ct) > 0;

    public async Task UpsertAsync(IrlValue value, CancellationToken ct)
    {
        var key = value.Quarter.ToString();
        var row = await db.Irl.FindAsync([key], ct);
        if (row is null)
            db.Irl.Add(row = new IrlRow { Quarter = key });
        row.Value = value.Value;
        row.PublishedOn = value.PublishedOn;
        row.FetchedOn = value.FetchedOn;
        row.Source = value.Source;
        await db.SaveChangesAsync(ct);
    }
}
