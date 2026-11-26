using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application.Spending;

/// <summary>
/// #85 decision 4: the month's spending (decision 3, the scope's accounts) less what the margin already took out: the lines linked to
/// an item, and the « Impôts & taxes » category with its children (the margin counts the tax, and a tax payment cannot be linked).
/// </summary>
public sealed class DashboardSpending(
    BankingAccess access, SpendingAccounts scopes, ITransactionStore lines, ICategoryStore categories, IHouseholdSettings household,
    IActorAccessor actor)
    : ISpendingContribution
{
    public const string Uncategorised = "Non classées";
    private const string Taxes = "taxes";

    public async Task<Result<SpendingPart>> EverydayAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        // The accounts are the actor's: read for anyone else (or for no one, the system), nothing rather than the actor's lines.
        if (actor.Current?.PersonId != reader || !(await access.EnabledAsync(ct)).IsSuccess)
            return Result.Success(SpendingPart.None);
        var (counted, unattached) = await scopes.ReadAsync(scope, ct);
        if (counted.Count == 0)
            return Result.Success(SpendingPart.None);
        IReadOnlyList<Guid> accounts = [.. counted.Select(a => a.Id)];
        var byId = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        // Seeded at zero: an account that spent nothing yet reads 0, not « nothing to read ».
        var total = new Dictionary<Currency, decimal> { [await household.ReferenceCurrencyAsync(ct)] = 0m };
        var slices = new Dictionary<Guid, Dictionary<Currency, decimal>>();
        var month = today.ToYearMonth().ToDateInterval();
        foreach (var sum in await lines.SpendingAsync(accounts, month.Start, month.End, ct, withLinked: false))
        {
            if (Everyday(sum, byId) is not { } amount || amount == 0m)
                continue;
            var top = Top(sum, byId);
            total[sum.Currency] = total.GetValueOrDefault(sum.Currency) + amount;
            var slice = slices.TryGetValue(top, out var found) ? found : slices[top] = [];
            slice[sum.Currency] = slice.GetValueOrDefault(sum.Currency) + amount;
        }
        var cards = await scopes.WithLinesAsync(unattached, month.Start, month.End, ct);
        return Result.Success(new SpendingPart(total,
            [.. slices.Select(s => s.Key == Guid.Empty ? new SpendingSlice(null, Uncategorised, s.Value) : new SpendingSlice(s.Key, byId[s.Key].Name, s.Value))],
            [.. cards.Select(c => new SpendingCard(c.Id, c.Name))]));
    }

    /// <summary>
    /// What <paramref name="sum"/> (of lines not linked to an item) adds to the everyday spending: its spending, none in the tax category.
    /// Issue #425: the spending page's usual everyday month counts the same.
    /// </summary>
    internal static decimal? Everyday(SpendingSum sum, IReadOnlyDictionary<Guid, BankCategory> byId)
        => Top(sum, byId) is var top && top != Guid.Empty && byId[top].Key == Taxes ? null : GetSpendingHandler.Spending(sum, byId);

    /// <summary>The top-level category of <paramref name="sum"/>; Guid.Empty for none.</summary>
    private static Guid Top(SpendingSum sum, IReadOnlyDictionary<Guid, BankCategory> byId)
        => sum.Category is { } id && byId.TryGetValue(id, out var category) ? category.Parent ?? category.Id : Guid.Empty;
}
