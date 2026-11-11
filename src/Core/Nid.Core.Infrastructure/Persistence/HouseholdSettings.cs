using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Persistence;

public sealed partial class HouseholdSettings(CoreDbContext db) : IHouseholdSettings
{
    [JsonSerializable(typeof(Dictionary<string, decimal>))]
    [JsonSerializable(typeof(IReadOnlyDictionary<string, decimal>))]
    [JsonSerializable(typeof(ProjectionAssumptions))]
    [JsonSerializable(typeof(FeeReferences))]
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
            row.MaxAdults, Currency.TryCreate(row.ReferenceCurrency.Trim(), out var currency) ? currency : Currency.Eur, adults, row.PricesEnabled, row.IrlEnabled,
            row.BankingEnabled, row.EnableBankingAppId, row.BankRetentionMonths, row.InflationEnabled);
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

        if (change.BankRetentionMonths is < 1 or > 600)
            return Result.Failure<HouseholdSettingsInfo>(FieldErrors.At(
                new Failure(PersonCodes.InvalidBankRetention, "Bank transactions are kept between 1 and 600 months.", null), "bankRetentionMonths"));

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

        if (change.InflationEnabled is { } inflation)
            row.InflationEnabled = inflation;

        if (change.BankingEnabled is { } banking)
            row.BankingEnabled = banking;

        if (change.EnableBankingAppId is { } appId)
            row.EnableBankingAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();

        if (change.ClearBankRetention)
            row.BankRetentionMonths = null;
        else if (change.BankRetentionMonths is { } months)
            row.BankRetentionMonths = months;

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

    /// <summary>Serialises the partial updates of the assumptions (« NID » + 2); the settings row may not exist yet, so no row lock.</summary>
    private const long AssumptionsLockKey = 0x4E49_4402;

    public async Task<ProjectionAssumptions> ProjectionAssumptionsAsync(CancellationToken ct)
        => Assumptions(await db.HouseholdSettings.AsNoTracking().Select(s => s.ProjectionAssumptions).FirstOrDefaultAsync(ct));

    public async Task<Result<ProjectionAssumptions>> UpdateProjectionAssumptionsAsync(
        Func<ProjectionAssumptions, Result<ProjectionAssumptions>> change, CancellationToken ct)
    {
        // An outer transaction holds the lock to its own end.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({AssumptionsLockKey})", ct);
        // Read past the change tracker: a row it already holds could be older than the lock.
        var changed = change(await ProjectionAssumptionsAsync(ct));
        if (!changed.TryGetValue(out var assumptions)) return changed;
        var row = await db.HouseholdSettings.FirstOrDefaultAsync(ct);
        if (row is null)
            db.HouseholdSettings.Add(row = new HouseholdSettingsRow());
        row.ProjectionAssumptions = JsonSerializer.Serialize(assumptions, HouseholdJson.Default.ProjectionAssumptions);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return changed;
    }

    private static ProjectionAssumptions Assumptions(string? json)
    {
        var saved = json is null ? null : JsonSerializer.Deserialize(json, HouseholdJson.Default.ProjectionAssumptions);
        if (saved is null) return ProjectionDefaults.Assumptions;
        // A class saved before it existed (or left out) takes its default rate, never a silent 0 %.
        var rates = new Dictionary<string, decimal>(ProjectionDefaults.ClassRates);
        foreach (var (name, rate) in saved.ClassRates) rates[name] = rate;
        return saved with { ClassRates = rates };
    }

    // Under the same lock: a whole replace never lands between a partial update's read and its write.
    public Task SaveProjectionAssumptionsAsync(ProjectionAssumptions assumptions, CancellationToken ct)
        => UpdateProjectionAssumptionsAsync(_ => Result.Success(assumptions), ct);

    public async Task<FeeReferences?> FeeReferencesAsync(CancellationToken ct)
    {
        var json = await db.HouseholdSettings.AsNoTracking().Select(s => s.FeeReferences).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize(json, HouseholdJson.Default.FeeReferences);
    }

    public async Task SaveFeeReferencesAsync(FeeReferences references, CancellationToken ct)
    {
        var row = await db.HouseholdSettings.FirstOrDefaultAsync(ct);
        if (row is null)
            db.HouseholdSettings.Add(row = new HouseholdSettingsRow());
        row.FeeReferences = JsonSerializer.Serialize(references, HouseholdJson.Default.FeeReferences);
        await db.SaveChangesAsync(ct);
    }

    private static Result<HouseholdSettingsInfo> Fail(string code, string message)
        => Result.Failure<HouseholdSettingsInfo>(new Failure(code, message, null));
}
