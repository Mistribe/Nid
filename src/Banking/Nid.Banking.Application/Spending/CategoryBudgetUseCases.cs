using System.Globalization;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application.Spending;

/// <summary>#85: the household's budgets on categories in use (an archived category keeps its budget, hidden), in the categories' order.</summary>
public sealed record ListBudgetsQuery : IQuery<IReadOnlyList<CategoryBudgetView>>;

/// <summary>
/// Creates or replaces the budget of an expense category in use. <paramref name="Amount"/>: an invariant decimal of zero or more, or null
/// to follow what is committed; <paramref name="Period"/>: <c>month</c> or <c>year</c>. One budget per branch: a parent's excludes its children's.
/// </summary>
public sealed record SetBudgetCommand(Guid CategoryId, string? Amount, string Period) : ICommand<CategoryBudgetView>, ITransactional, IRequiresBanking;

public sealed record DeleteBudgetCommand(Guid CategoryId) : ICommand<Guid>, ITransactional, IRequiresBanking;

/// <summary>
/// #427: several budgets created at once, all or none. Each as <see cref="SetBudgetCommand"/>, with <see cref="NewBudget.Source"/> saying
/// which: <c>typed</c> with an amount, <c>committed</c> without. A category whose branch already holds a budget, or one asked twice, is
/// refused: nothing here replaces a budget.
/// </summary>
public sealed record CreateBudgetsCommand(IReadOnlyList<NewBudget> Budgets) : ICommand<IReadOnlyList<CategoryBudgetView>>, ITransactional, IRequiresBanking;

public sealed record NewBudget(Guid CategoryId, string? Amount, string? Source, string? Period);

/// <summary>
/// #427: the budgets proposed to the household (budgets are its own, so from its « Foyer » figures of the current month): the active
/// top-level expense categories whose branch holds none and whose Mois habituel (<see cref="SpendingView.Average"/>) is above zero, most
/// spent first; none without that average.
/// </summary>
public sealed record SuggestBudgetsQuery : IQuery<IReadOnlyList<BudgetSuggestionView>>, IRequiresBanking;

/// <summary>
/// <paramref name="Average"/>: the Mois habituel; <paramref name="Proposed"/>: it rounded up to the next 10, in the reference currency;
/// <paramref name="Committed"/>: something is committed there this month, so « Suivre mes engagements » is offered instead of an amount;
/// <paramref name="Preselected"/>: one of the 8 biggest.
/// </summary>
public sealed record BudgetSuggestionView(Guid CategoryId, string Name, string Average, string Proposed, bool Committed, bool Preselected);

/// <summary><paramref name="Amount"/> in the household's reference currency, null when it follows what is committed; <paramref name="Period"/> is <c>month</c> or <c>year</c>.</summary>
public sealed record CategoryBudgetView(Guid CategoryId, string CategoryName, string? Amount, string Period);

[RequestHandler<ListBudgetsQuery, IReadOnlyList<CategoryBudgetView>>]
public sealed class ListBudgetsHandler(ICategoryStore categories, ICategoryBudgetStore budgets) : IRequestHandler<ListBudgetsQuery, IReadOnlyList<CategoryBudgetView>>
{
    public async ValueTask<Result<IReadOnlyList<CategoryBudgetView>>> HandleAsync(ListBudgetsQuery query, CancellationToken ct = default)
    {
        var all = (await budgets.AllAsync(ct)).ToDictionary(b => b.Category);
        return Result.Success<IReadOnlyList<CategoryBudgetView>>([.. (await categories.AllAsync(ct))
            .Where(c => c.ArchivedAt is null && all.ContainsKey(c.Id))
            .Select(c => SetBudgetHandler.View(all[c.Id], c.Name))]);
    }
}

[RequestHandler<SetBudgetCommand, CategoryBudgetView>]
public sealed class SetBudgetHandler(ICategoryStore categories, ICategoryBudgetStore budgets)
    : IRequestHandler<SetBudgetCommand, CategoryBudgetView>
{
    public async ValueTask<Result<CategoryBudgetView>> HandleAsync(SetBudgetCommand command, CancellationToken ct = default)
    {
        var all = await categories.AllAsync(ct);
        var checkedBudget = Check(command.CategoryId, command.Amount, command.Period, "", all, (await budgets.AllAsync(ct)).Select(b => b.Category), replace: true);
        if (!checkedBudget.TryGetValue(out var budget))
            return BankingAccess.Failed<CategoryBudgetView, CategoryBudget>(checkedBudget);
        await budgets.SetAsync(budget, ct);
        return Result.Success(View(budget, all.First(c => c.Id == budget.Category).Name));
    }

    /// <summary>
    /// The budget asked for, or why not, its field failures located under <paramref name="at"/>. <paramref name="held"/>: the categories
    /// with a budget; with <paramref name="replace"/> the category's own is replaced, else it is refused.
    /// </summary>
    internal static Result<CategoryBudget> Check(
        Guid categoryId, string? amountText, string? periodText, string at, IReadOnlyList<BankCategory> all, IEnumerable<Guid> held, bool replace)
    {
        var period = periodText switch { "month" => BudgetPeriod.Month, "year" => BudgetPeriod.Year, _ => (BudgetPeriod?)null };
        if (period is null)
            return Result.Failure<CategoryBudget>(FieldErrors.Shape([new FieldError($"{at}period", FieldErrors.ShapeCode, "A period is month or year.")]));
        decimal? amount = null;
        if (amountText is { } text)
        {
            // numeric(19,4) holds under 10^15 with 4 decimals, refused past them rather than rounded (#634); no sign is read, so a negative
            // amount is refused with the unreadable ones.
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed >= 1_000_000_000_000_000m
                || decimal.Round(parsed, 4) != parsed)
                return Result.Failure<CategoryBudget>(FieldErrors.At(
                    new Failure(BankingCodes.BudgetAmount, "A budget is an amount of zero or more, with at most 4 decimals.", null), $"{at}amount"));
            amount = parsed;
        }

        if (all.FirstOrDefault(c => c.Id == categoryId) is not { } category)
            return Result.Failure<CategoryBudget>(new NotFoundFailure("bank category", categoryId.ToString()));
        if (category.ArchivedAt is not null)
            return BankingAccess.Fail<CategoryBudget>(BankingCodes.CategoryArchived, "A budget goes on a category in use.");
        if (category.Kind != CategoryKind.Expense)
            return BankingAccess.Fail<CategoryBudget>(BankingCodes.CategoryKind, "A budget goes on an expense category.");
        // An archived category's budget is hidden and inert: it never blocks.
        // ponytail: read then write, no lock: two adults budgeting a parent and its child at once both pass, and two first PUTs on one
        // category at once hit the unique index (a 500); upgrade with an advisory lock.
        if (!replace && held.Contains(category.Id))
            return BankingAccess.Fail<CategoryBudget>(BankingCodes.BudgetExists, "This category already has a budget.");
        var branch = all.Where(c => c.ArchivedAt is null && (c.Id == category.Parent || c.Parent == category.Id)).Select(c => c.Id).ToHashSet();
        if (held.Any(branch.Contains))
            return BankingAccess.Fail<CategoryBudget>(BankingCodes.BudgetBranch, "Its parent or one of its children has a budget: one per branch.");
        return Result.Success(new CategoryBudget(category.Id, amount, period.Value));
    }

    internal static CategoryBudgetView View(CategoryBudget b, string name)
        => new(b.Category, name, b.Amount is { } amount ? TransactionLinker.Amount(amount) : null, b.Period == BudgetPeriod.Month ? "month" : "year");
}

[RequestHandler<DeleteBudgetCommand, Guid>]
public sealed class DeleteBudgetHandler(ICategoryBudgetStore budgets) : IRequestHandler<DeleteBudgetCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteBudgetCommand command, CancellationToken ct = default)
    {
        return await budgets.DeleteAsync(command.CategoryId, ct)
            ? Result.Success(command.CategoryId)
            : Result.Failure<Guid>(new NotFoundFailure("budget", command.CategoryId.ToString()));
    }
}

[RequestHandler<CreateBudgetsCommand, IReadOnlyList<CategoryBudgetView>>]
public sealed class CreateBudgetsHandler(ICategoryStore categories, ICategoryBudgetStore budgets)
    : IRequestHandler<CreateBudgetsCommand, IReadOnlyList<CategoryBudgetView>>
{
    /// <summary>More than a household's top-level categories ever are.</summary>
    public const int Max = 100;

    public async ValueTask<Result<IReadOnlyList<CategoryBudgetView>>> HandleAsync(CreateBudgetsCommand command, CancellationToken ct = default)
    {
        if (command.Budgets is not { Count: > 0 and <= Max })
            return Shape("budgets", $"Send 1 to {Max} budgets.");

        var all = await categories.AllAsync(ct);
        // The batch's own count as held too: two of it on one branch are refused as an existing one would be.
        var held = (await budgets.AllAsync(ct)).Select(b => b.Category).ToList();
        var created = new List<CategoryBudget>();
        foreach (var (asked, i) in command.Budgets.Select((b, i) => (b, i)))
        {
            var at = $"budgets[{i}].";
            if (asked.Source is not ("typed" or "committed"))
                return Shape($"{at}source", "A source is typed or committed.");
            if (asked.Source == "committed" && asked.Amount is not null)
                return Shape($"{at}amount", "A budget that follows the commitments has no amount.");
            // A typed budget without an amount is refused as an unreadable one.
            var checkedBudget = SetBudgetHandler.Check(
                asked.CategoryId, asked.Source == "typed" ? asked.Amount ?? "" : null, asked.Period, at, all, held, replace: false);
            if (!checkedBudget.TryGetValue(out var budget))
            {
                checkedBudget.TryGetFailure(out var failure);
                // A rule on the category names which one; a field's failure is located already, a missing category stays a 404.
                return Result.Failure<IReadOnlyList<CategoryBudgetView>>(FieldErrors.Of(failure!) is null ? FieldErrors.At(failure!, $"{at}categoryId") : failure!);
            }
            held.Add(budget.Category);
            created.Add(budget);
        }

        // Checked as one, then written in the command's one transaction.
        foreach (var budget in created)
            await budgets.SetAsync(budget, ct);
        return Result.Success<IReadOnlyList<CategoryBudgetView>>([.. created.Select(b => SetBudgetHandler.View(b, all.First(c => c.Id == b.Category).Name))]);
    }

    private static Result<IReadOnlyList<CategoryBudgetView>> Shape(string path, string message)
        => Result.Failure<IReadOnlyList<CategoryBudgetView>>(FieldErrors.Shape([new FieldError(path, FieldErrors.ShapeCode, message)]));
}

[RequestHandler<SuggestBudgetsQuery, IReadOnlyList<BudgetSuggestionView>>]
public sealed class SuggestBudgetsHandler(GetSpendingHandler spending, ICategoryStore categories, ICategoryBudgetStore budgets, CommittedByCategory committed)
    : IRequestHandler<SuggestBudgetsQuery, IReadOnlyList<BudgetSuggestionView>>
{
    public async ValueTask<Result<IReadOnlyList<BudgetSuggestionView>>> HandleAsync(SuggestBudgetsQuery query, CancellationToken ct = default)
    {
        var figures = await spending.SpentAsync(new GetSpendingQuery(VisibilityScope.Household, null, null, null), ct);
        if (!figures.TryGetValue(out var sums))
            return BankingAccess.Failed<IReadOnlyList<BudgetSuggestionView>, GetSpendingHandler.Spent>(figures);
        // No Mois habituel, nothing to propose (as the page shows none).
        if (sums.AverageMissing || sums.Covered < GetSpendingHandler.MinHistoryMonths)
            return Result.Success<IReadOnlyList<BudgetSuggestionView>>([]);

        var all = await categories.AllAsync(ct);
        // Committed there this month: what the page already follows when no budget is set.
        var (commitments, _) = await committed.ForAsync(sums.From, sums.Reference, sums.Today, all.ToDictionary(c => c.Id), ct);
        var suggested = BudgetSuggestions.For(all, (await budgets.AllAsync(ct)).Select(b => b.Category).ToHashSet(),
            sums.ByCategory.ToDictionary(p => p.Key, p => p.Value.Before / sums.Covered),
            commitments.Where(c => Math.Round(c.Value.Month, 2, MidpointRounding.AwayFromZero) > 0m).Select(c => c.Key).ToHashSet(),
            GetSpendingHandler.ByName);
        return Result.Success<IReadOnlyList<BudgetSuggestionView>>([.. suggested.Select(s => new BudgetSuggestionView(
            s.Category, s.Name, GetSpendingHandler.Money(s.Average), GetSpendingHandler.Money(s.Proposed), s.Committed, s.Preselected))]);
    }
}
