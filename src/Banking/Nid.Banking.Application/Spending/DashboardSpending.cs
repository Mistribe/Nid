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
        if (actor.Current?.PersonId != reader || !(await access.EnabledAsync(ct)).IsSuccess
            || await scopes.ForAsync(scope, ct) is not { Count: > 0 } accounts)
            return Result.Success(SpendingPart.None);
        var byId = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        // Seeded at zero: an account that spent nothing yet reads 0, not « nothing to read ».
        var total = new Dictionary<Currency, decimal> { [await household.ReferenceCurrencyAsync(ct)] = 0m };
        var slices = new Dictionary<Guid, Dictionary<Currency, decimal>>();
        var month = today.ToYearMonth().ToDateInterval();
        foreach (var sum in await lines.SpendingAsync(accounts, month.Start, month.End, ct, withLinked: false))
        {
            // Guid.Empty: no category.
            var top = sum.Category is { } id && byId.TryGetValue(id, out var category) ? category.Parent ?? category.Id : Guid.Empty;
            if (top != Guid.Empty && byId[top].Key == Taxes || GetSpendingHandler.Spending(sum, byId) is not { } amount || amount == 0m)
                continue;
            total[sum.Currency] = total.GetValueOrDefault(sum.Currency) + amount;
            var slice = slices.TryGetValue(top, out var found) ? found : slices[top] = [];
            slice[sum.Currency] = slice.GetValueOrDefault(sum.Currency) + amount;
        }
        return Result.Success(new SpendingPart(total,
            [.. slices.Select(s => s.Key == Guid.Empty ? new SpendingSlice(null, Uncategorised, s.Value) : new SpendingSlice(s.Key, byId[s.Key].Name, s.Value))]));
    }
}
