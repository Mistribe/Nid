using System.Globalization;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
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
public sealed record SetBudgetCommand(Guid CategoryId, string? Amount, string Period) : ICommand<CategoryBudgetView>, ITransactional;

public sealed record DeleteBudgetCommand(Guid CategoryId) : ICommand<Guid>, ITransactional;

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
public sealed class SetBudgetHandler(BankingAccess access, ICategoryStore categories, ICategoryBudgetStore budgets)
    : IRequestHandler<SetBudgetCommand, CategoryBudgetView>
{
    public async ValueTask<Result<CategoryBudgetView>> HandleAsync(SetBudgetCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<CategoryBudgetView>(enabled);
        var period = command.Period switch { "month" => BudgetPeriod.Month, "year" => BudgetPeriod.Year, _ => (BudgetPeriod?)null };
        if (period is null)
            return Result.Failure<CategoryBudgetView>(FieldErrors.Shape([new FieldError("period", FieldErrors.ShapeCode, "A period is month or year.")]));
        decimal? amount = null;
        if (command.Amount is { } text)
        {
            // numeric(19,4) holds under 10^15; no sign is read, so a negative amount is refused with the unreadable ones.
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed >= 1_000_000_000_000_000m)
                return Result.Failure<CategoryBudgetView>(FieldErrors.At(
                    new Failure(BankingCodes.BudgetAmount, "A budget is an amount of zero or more.", null), "amount"));
            amount = parsed;
        }

        var all = await categories.AllAsync(ct);
        if (all.FirstOrDefault(c => c.Id == command.CategoryId) is not { } category)
            return Result.Failure<CategoryBudgetView>(new NotFoundFailure("bank category", command.CategoryId.ToString()));
        if (category.ArchivedAt is not null)
            return BankingAccess.Fail<CategoryBudgetView>(BankingCodes.CategoryArchived, "A budget goes on a category in use.");
        if (category.Kind != CategoryKind.Expense)
            return BankingAccess.Fail<CategoryBudgetView>(BankingCodes.CategoryKind, "A budget goes on an expense category.");
        // An archived category's budget is hidden and inert: it never blocks.
        // ponytail: read then write, no lock: two adults budgeting a parent and its child at once both pass, and two first PUTs on one
        // category at once hit the unique index (a 500); upgrade with an advisory lock.
        var branch = all.Where(c => c.ArchivedAt is null && (c.Id == category.Parent || c.Parent == category.Id)).Select(c => c.Id).ToHashSet();
        if ((await budgets.AllAsync(ct)).Any(b => branch.Contains(b.Category)))
            return BankingAccess.Fail<CategoryBudgetView>(BankingCodes.BudgetBranch, "Its parent or one of its children has a budget: one per branch.");

        var budget = new CategoryBudget(category.Id, amount, period.Value);
        await budgets.SetAsync(budget, ct);
        return Result.Success(View(budget, category.Name));
    }

    internal static CategoryBudgetView View(CategoryBudget b, string name)
        => new(b.Category, name, b.Amount is { } amount ? TransactionLinker.Amount(amount) : null, b.Period == BudgetPeriod.Month ? "month" : "year");
}

[RequestHandler<DeleteBudgetCommand, Guid>]
public sealed class DeleteBudgetHandler(BankingAccess access, ICategoryBudgetStore budgets) : IRequestHandler<DeleteBudgetCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteBudgetCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<Guid>(enabled);
        return await budgets.DeleteAsync(command.CategoryId, ct)
            ? Result.Success(command.CategoryId)
            : Result.Failure<Guid>(new NotFoundFailure("budget", command.CategoryId.ToString()));
    }
}
