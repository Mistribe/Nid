using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class HouseholdSettings(CoreDbContext db) : IHouseholdSettings
{
    public async Task<Currency> ReferenceCurrencyAsync(CancellationToken ct)
    {
        var code = await db.HouseholdSettings.AsNoTracking().Select(s => s.ReferenceCurrency).FirstOrDefaultAsync(ct);
        return Currency.TryCreate(code?.Trim(), out var currency) ? currency : Currency.Eur;
    }
}
