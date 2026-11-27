using System.Globalization;
using NodaTime;

namespace Nid.Core.Contracts;

/// <summary>
/// #794: a list's rows gathered by owners and shares, each group's amounts summed in the household's currency, every amount
/// converted today and rounded to the cent. An amount a row lacks adds nothing; one whose rate is missing, or none at all, leaves
/// the total unknown.
/// </summary>
public sealed class OwnerGroups
{
    private readonly RateTable rates;
    private readonly LocalDate today;

    private OwnerGroups(Currency reference, RateTable rates, LocalDate today)
        => (Reference, this.rates, this.today) = (reference, rates, today);

    public Currency Reference { get; }

    /// <param name="currencies">The currencies the amounts to sum are in: their rates are read once.</param>
    public static async Task<OwnerGroups> LoadAsync(
        IHouseholdSettings household, IFxRates fx, IEnumerable<string> currencies, LocalDate today, CancellationToken ct)
    {
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = currencies.Select(c => Currency.TryCreate(c, out var currency) ? currency : reference).Where(c => c != reference).Distinct().ToList();
        // Rates are per USD: the reference's own quote is needed too.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return new(reference, rates, today);
    }

    /// <summary>The rows by owners and shares, in the rows' order.</summary>
    public static IEnumerable<IGrouping<string, T>> Of<T>(IEnumerable<T> rows, Func<T, IReadOnlyList<ItemOwnerView>> owners)
        => rows.GroupBy(r => string.Join(',', owners(r).OrderBy(o => o.PersonId).Select(o => $"{o.PersonId}:{o.ShareBp}")));

    /// <summary>The amounts (invariant text, in their currency) summed in <see cref="Reference"/>, as <c>0.00</c>; null as the class says.</summary>
    public string? Sum(IEnumerable<(string? Amount, string Currency)> amounts)
    {
        var total = 0m;
        var any = false;
        foreach (var (text, code) in amounts)
        {
            if (text is null) continue;
            if (!Currency.TryCreate(code, out var currency)) return null;
            var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
            if (currency != Reference)
            {
                if (!rates.TryConvert(amount, currency, Reference, today, projected: false, out var converted, out _)) return null;
                amount = converted;
            }
            total += Math.Round(amount, 2, MidpointRounding.AwayFromZero);
            any = true;
        }
        return any ? total.ToString("0.00", CultureInfo.InvariantCulture) : null;
    }
}
