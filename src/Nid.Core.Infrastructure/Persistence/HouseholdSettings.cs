using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed partial class HouseholdSettings(CoreDbContext db) : IHouseholdSettings
{
    [JsonSerializable(typeof(Dictionary<string, decimal>))]
    [JsonSerializable(typeof(IReadOnlyDictionary<string, decimal>))]
    private sealed partial class HouseholdJson : JsonSerializerContext;

    public async Task<Currency> ReferenceCurrencyAsync(CancellationToken ct)
    {
        var code = await db.HouseholdSettings.AsNoTracking().Select(s => s.ReferenceCurrency).FirstOrDefaultAsync(ct);
        return Currency.TryCreate(code?.Trim(), out var currency) ? currency : Currency.Eur;
    }

    public async Task<HouseholdSettingsInfo> ReadAsync(CancellationToken ct)
    {
        var row = await db.HouseholdSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new HouseholdSettingsRow();
        var adults = await db.Persons.CountAsync(p => p.Kind == "adult", ct);
        return new HouseholdSettingsInfo(
            row.MaxAdults, Currency.TryCreate(row.ReferenceCurrency.Trim(), out var currency) ? currency : Currency.Eur, adults, row.PricesEnabled, row.IrlEnabled);
    }

    public async Task<Result<HouseholdSettingsInfo>> UpdateAsync(HouseholdSettingsChange change, CancellationToken ct)
    {
        Currency? currency = null;
        if (change.ReferenceCurrency is not null)
        {
            if (!Currency.TryCreate(change.ReferenceCurrency, out var parsed))
                return Fail(PersonCodes.UnknownCurrency, "The reference currency is a three-letter ISO 4217 code, in capitals.");
            currency = parsed;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The same lock as pre-registration: a cap lowered while an adult is being added must see that adult.
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({PersonRegistry.ProvisioningLockKey})", ct);

        var row = await db.HouseholdSettings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new HouseholdSettingsRow();
            db.HouseholdSettings.Add(row);
        }

        if (change.MaxAdults is { } cap)
        {
            var adults = await db.Persons.CountAsync(p => p.Kind == "adult", ct);
            if (cap < 1 || cap < adults)
                return Fail(PersonCodes.CapBelowCount, $"The cap cannot be lower than 1, nor than the {adults} adults the household already has.");
            row.MaxAdults = cap;
        }

        if (currency is { } chosen)
            row.ReferenceCurrency = chosen.Code;

        if (change.PricesEnabled is { } prices)
            row.PricesEnabled = prices;

        if (change.IrlEnabled is { } irl)
            row.IrlEnabled = irl;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success(await ReadAsync(ct));
    }

    public async Task<IReadOnlyDictionary<string, decimal>> AllocationTargetAsync(CancellationToken ct)
    {
        var json = await db.HouseholdSettings.AsNoTracking().Select(s => s.AllocationTarget).FirstOrDefaultAsync(ct);
        return json is null ? new Dictionary<string, decimal>() : JsonSerializer.Deserialize(json, HouseholdJson.Default.DictionaryStringDecimal) ?? [];
    }

    public async Task SaveAllocationTargetAsync(IReadOnlyDictionary<string, decimal> target, CancellationToken ct)
    {
        var row = await db.HouseholdSettings.FirstOrDefaultAsync(ct);
        if (row is null)
            db.HouseholdSettings.Add(row = new HouseholdSettingsRow());
        row.AllocationTarget = target.Count == 0 ? null : JsonSerializer.Serialize(target, HouseholdJson.Default.IReadOnlyDictionaryStringDecimal);
        await db.SaveChangesAsync(ct);
    }

    private static Result<HouseholdSettingsInfo> Fail(string code, string message)
        => Result.Failure<HouseholdSettingsInfo>(new Failure(code, message, null));
}
