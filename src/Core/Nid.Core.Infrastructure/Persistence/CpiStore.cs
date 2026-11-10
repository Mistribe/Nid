using Microsoft.EntityFrameworkCore;
using Nid.Core.Domain.Inflation;
using NodaTime.Text;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class CpiStore(CoreDbContext db) : ICpiStore
{
    public async Task<IReadOnlyList<CpiValue>> ListAsync(CancellationToken ct)
        => (await db.Cpi.AsNoTracking().OrderBy(r => r.Month).ToListAsync(ct))
            .Select(r => new CpiValue(YearMonthPattern.Iso.Parse(r.Month).GetValueOrThrow(), r.Value, r.PublishedOn, r.FetchedOn, r.Source)).ToList();

    public async Task<(IReadOnlyList<CpiValue> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct)
    {
        var rows = await db.Cpi.AsNoTracking().OrderByDescending(r => r.Month).Skip(skip).Take(take).ToListAsync(ct);
        return (rows.Select(r => new CpiValue(YearMonthPattern.Iso.Parse(r.Month).GetValueOrThrow(), r.Value, r.PublishedOn, r.FetchedOn, r.Source)).ToList(), await db.Cpi.CountAsync(ct));
    }

    public async Task<bool> UpsertManualAsync(CpiValue value, CancellationToken ct)
        => await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into core.cpi (month, value, published_on, fetched_on, source)
            values ({YearMonthPattern.Iso.Format(value.Month)}, {value.Value}, {value.PublishedOn}, {value.FetchedOn}, 'manual')
            on conflict (month) do update
              set value = excluded.value, published_on = excluded.published_on, fetched_on = excluded.fetched_on, source = 'manual'
              where core.cpi.source <> 'insee'
            """, ct) > 0;

    public async Task UpsertAsync(CpiValue value, CancellationToken ct) => await UpsertManyAsync([value], ct);

    public async Task UpsertManyAsync(IReadOnlyList<CpiValue> values, CancellationToken ct)
    {
        var keys = values.Select(v => YearMonthPattern.Iso.Format(v.Month)).ToList();
        var rows = await db.Cpi.Where(r => keys.Contains(r.Month)).ToDictionaryAsync(r => r.Month, ct);
        foreach (var value in values)
        {
            var key = YearMonthPattern.Iso.Format(value.Month);
            if (!rows.TryGetValue(key, out var row))
                db.Cpi.Add(rows[key] = row = new CpiRow { Month = key });
            row.Value = value.Value;
            row.PublishedOn = value.PublishedOn;
            row.FetchedOn = value.FetchedOn;
            row.Source = value.Source;
        }
        await db.SaveChangesAsync(ct); // one SaveChanges is one transaction
    }
}
