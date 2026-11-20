using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application.Operations;

/// <summary>
/// #84: what narrows the operations list. Empty lists and nulls narrow nothing. <paramref name="Accounts"/> and <paramref name="Owners"/> are
/// the reader's own pick among what they read: an id they cannot see is ignored. <paramref name="Min"/> and <paramref name="Max"/> bound the
/// absolute amount; <paramref name="State"/> is a <c>link_state</c> wire value; <paramref name="Direction"/> is <c>out</c> or <c>in</c>;
/// <paramref name="Search"/> is a substring of the label or the counterparty, case and accents aside. A parent category takes its children,
/// and <paramref name="Uncategorised"/> adds the lines with none.
/// </summary>
public sealed record OperationFilter(
    IReadOnlyList<Guid> Accounts, IReadOnlyList<Guid> Owners, LocalDate? From, LocalDate? To, IReadOnlyList<Guid> Categories,
    bool Uncategorised, string? State, string? Direction, decimal? Min, decimal? Max, string? Search);

/// <summary>One page (<see cref="BankLines.PageSize"/>, newest booked first, <paramref name="Page"/> from 1) of the lines the actor reads, whatever their account.</summary>
public sealed record ListOperationsQuery(OperationFilter Filter, int Page) : IQuery<OperationPage>;

/// <summary><paramref name="Total"/> counts every line the filter keeps, not only the page's; <paramref name="Totals"/> and <paramref name="Facets"/> are the filter's.</summary>
public sealed record OperationPage(IReadOnlyList<BankTransactionView> Items, int Total, OperationTotals Totals, OperationFacets Facets,
    IReadOnlyList<AccountOwnersView> AccountOwners);

/// <summary>Who owns a readable account, as the accounts list shows it (the joint account has both adults): the page's owner column;
/// <paramref name="Writable"/> when the reader may edit its lines.</summary>
public sealed record AccountOwnersView(Guid AccountId, IReadOnlyList<OptionView> Owners, bool Writable);

/// <summary>
/// The filter's money out (positive), in and net (signed) in the household's reference currency, internal and ignored lines and ignored accounts
/// left out; <paramref name="Incomplete"/> when a line had no rate and was left out too.
/// </summary>
public sealed record OperationTotals(string Out, string In, string Net, string Currency, bool Incomplete);

/// <summary>What the filter can pick from, never beyond what the actor reads.</summary>
public sealed record OperationFacets(IReadOnlyList<OptionView> Accounts, IReadOnlyList<OptionView> Owners, IReadOnlyList<OptionView> Categories);

public sealed record OptionView(Guid Id, string Name);

[RequestHandler<ListOperationsQuery, OperationPage>]
public sealed class ListOperationsHandler(
    BankingAccess access, IAccountStore accounts, ITransactionStore transactions, ICategoryStore categories, BankLines lines,
    IPersonRegistry persons, IHouseholdSettings household, IHouseholdCalendar calendar, IFxRates fx)
    : IRequestHandler<ListOperationsQuery, OperationPage>
{
    public async ValueTask<Result<OperationPage>> HandleAsync(ListOperationsQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<OperationPage>(enabled);
        // « all » is no state at all, as for the other lists.
        var filter = query.Filter with { State = query.Filter.State == "all" ? null : query.Filter.State };
        if (Invalid(filter) is { Count: > 0 } errors)
            return Result.Failure<OperationPage>(FieldErrors.Shape(errors));

        var rights = await access.ReadableAsync(await accounts.AllAsync(ct), ct);
        var readable = rights.Select(r => r.Account).ToList();
        // Who reads each account is who owns it as the accounts list shows it: the joint account has both adults.
        var readers = await access.ReadersAsync(readable, ct);
        var scope = readable.Where(a => (filter.Accounts.Count == 0 || filter.Accounts.Contains(a.Id))
            && (filter.Owners.Count == 0 || readers[a.Id].Any(p => filter.Owners.Contains(p.Value)))).Select(a => a.Id).ToList();

        var all = await categories.AllAsync(ct);
        var wanted = all.Where(c => filter.Categories.Contains(c.Id) || (c.Parent is { } p && filter.Categories.Contains(p))).Select(c => c.Id).ToList();
        var (items, total, sums) = await transactions.OperationsAsync([.. readable.Select(a => a.Id)], filter with { Accounts = scope }, wanted,
            (Math.Clamp(query.Page, 1, BankLines.MaxPage) - 1) * BankLines.PageSize, BankLines.PageSize, ct);

        var totals = await TotalsAsync(sums, ct);
        var readableIds = readable.Select(a => a.Id).ToList();
        var used = (await transactions.OperationCategoriesAsync(readableIds, ct)).ToHashSet();
        var byId = all.ToDictionary(c => c.Id);
        var facetCategories = used.Concat(used.Where(byId.ContainsKey).Select(id => byId[id].Parent).OfType<Guid>()).Distinct()
            .Where(byId.ContainsKey).Select(id => new OptionView(id, byId[id].Name)).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var owners = readers.Values.SelectMany(r => r).ToHashSet();
        var people = (await persons.ListAsync(ct)).Where(p => owners.Contains(p.Id)).ToList();
        var facets = new OperationFacets(
            [.. readable.Select(a => new OptionView(a.Id, a.Name)).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)],
            [.. people.Select(p => new OptionView(p.Id.Value, p.DisplayName))],
            facetCategories);
        var accountOwners = readable.Select(a => new AccountOwnersView(a.Id,
            [.. people.Where(p => readers[a.Id].Contains(p.Id)).Select(p => new OptionView(p.Id.Value, p.DisplayName))],
            rights.Single(r => r.Account.Id == a.Id).CanWrite)).ToList();
        return Result.Success(new OperationPage(await lines.ViewsAsync(items, readable.ToDictionary(a => a.Id), ct), total, totals, facets, accountOwners));
    }

    private static List<FieldError>? Invalid(OperationFilter f)
    {
        var errors = new List<FieldError>();
        void Add(string field, string message) => errors.Add(new FieldError(field, FieldErrors.ShapeCode, message));
        if (f.State is not null && BankLines.State(f.State, "all") is { IsSuccess: false })
            Add("state", "A state is unlinked, linked, ignored, internal or all.");
        if (f.Direction is not (null or "out" or "in"))
            Add("direction", "A direction is out or in.");
        if (f.Min < 0m)
            Add("min", "A minimum amount is not negative.");
        if (f.Max < 0m)
            Add("max", "A maximum amount is not negative.");
        if (f.Min > f.Max)
            Add("min", "The minimum amount is not above the maximum.");
        if (f.From > f.To)
            Add("from", "The first day is not after the last.");
        if (f.Search is { Length: 1 })
            Add("q", "A search takes at least two characters.");
        return errors;
    }

    /// <summary>Like the spending view: a past month at its month-end rate, the current one at today's; a line with no rate is left out and flagged.</summary>
    private async Task<OperationTotals> TotalsAsync(IReadOnlyList<SpendingSum> sums, CancellationToken ct)
    {
        var reference = await household.ReferenceCurrencyAsync(ct);
        var today = await calendar.TodayAsync(ct);
        var foreign = sums.Select(s => s.Currency).Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0)
            : await fx.LoadAsync([.. foreign, reference], sums.Min(s => s.Month.ToDateInterval().End), today, ct);
        decimal debits = 0m, credits = 0m;
        var incomplete = false;
        foreach (var sum in sums)
        {
            if (!rates.TryConvert(sum.Debits, sum.Currency, reference, sum.Month.ToDateInterval().End, projected: true, out var debit, out _)
                || !rates.TryConvert(sum.Credits, sum.Currency, reference, sum.Month.ToDateInterval().End, projected: true, out var credit, out _))
            {
                incomplete = true;
                continue;
            }
            (debits, credits) = (debits + debit, credits + credit);
        }
        return new OperationTotals(Spending.GetSpendingHandler.Money(-debits), Spending.GetSpendingHandler.Money(credits),
            Spending.GetSpendingHandler.Money(credits + debits), reference.Code, incomplete);
    }
}
