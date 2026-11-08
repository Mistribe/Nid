using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>5C spec §6.1: every module's expected charges for a person, and their amounts in a line's currency (D25).</summary>
public sealed class ExpectedChargeReader(IEnumerable<IExpectedCharges> sources, IAccessPolicy policy, IFxRates fx, IAccountStore accounts)
{
    public async Task<IReadOnlyList<ExpectedCharge>> ReadableAsync(PersonId person, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var charges = new List<ExpectedCharge>();
        foreach (var source in sources)
            if ((await source.ChargesAsync(person, from, to, ct)).TryGetValue(out var found))
                charges.AddRange(found);
        return charges;
    }

    /// <summary>The readable charges of the items the person writes: the ones they may link a line to.</summary>
    public async Task<IReadOnlyList<ExpectedCharge>> WritableAsync(PersonId person, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var charges = await ReadableAsync(person, from, to, ct);
        var writable = await policy.CheckManyAsync(person, [.. charges.Select(c => c.Item).Distinct()], AccessKind.Write, ct);
        return [.. charges.Where(c => writable.Contains(c.Item))];
    }

    /// <summary>
    /// (charge, booked on, line currency) → the charge's amount in the line's currency at Nid's rate of that day; null without a rate.
    /// A line's currency is its account's, so the rates loaded are the charges' and the accounts' currencies.
    /// </summary>
    public async Task<Func<ExpectedCharge, LocalDate, string, decimal?>> ConverterAsync(
        IReadOnlyCollection<ExpectedCharge> charges, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var quotes = charges.Select(c => c.Amount.Currency).Concat((await accounts.AllAsync(ct)).Select(a => a.Currency)).ToHashSet();
        var rates = quotes.Count <= 1 ? null : await fx.LoadAsync(quotes, from, to, ct);
        return (charge, on, currency) =>
            charge.Amount.Currency.Code == currency ? charge.Amount.Amount
            : rates is not null && Currency.TryCreate(currency, out var target)
              && rates.TryConvert(charge.Amount.Amount, charge.Amount.Currency, target, on, projected: false, out var converted, out _)
                ? converted
                : null;
    }
}
