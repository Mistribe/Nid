using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>5C spec §6.1: every module's expected charges for a person, and their amounts in a line's currency (D25).</summary>
public sealed partial class ExpectedChargeReader(
    IEnumerable<IExpectedCharges> sources, IAccessPolicy policy, IFxRates fx, IAccountStore accounts, ILogger<ExpectedChargeReader> logger)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Expected charges from {Source} ({Scope}) failed with {Code}; its charges are left out.")]
    private partial void LogSourceFailed(string source, VisibilityScope scope, string? code);

    public Task<IReadOnlyList<ExpectedCharge>> ReadableAsync(PersonId person, LocalDate from, LocalDate to, CancellationToken ct)
        => InScopeAsync(person, VisibilityScope.Me, from, to, ct);

    /// <summary>Every module's charges on the items the person sees in <paramref name="scope"/>; a module that fails adds none.</summary>
    public async Task<IReadOnlyList<ExpectedCharge>> InScopeAsync(PersonId person, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var charges = new List<ExpectedCharge>();
        foreach (var source in sources)
        {
            var result = await source.ChargesAsync(person, scope, from, to, ct);
            if (result.TryGetValue(out var found))
                charges.AddRange(found);
            else if (result.TryGetFailure(out var failure))
                LogSourceFailed(source.GetType().Name, scope, failure?.Code);
        }
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
