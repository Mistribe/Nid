using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// #449: one page (<see cref="ListCategorySuggestionsHandler.PageSize"/>, <paramref name="Page"/> from 1) of the categories the household's
/// own choices suggest for the actor's uncategorised lines, the group covering the most lines first. Computed on every read; nothing applies.
/// </summary>
public sealed record ListCategorySuggestionsQuery(int Page) : IQuery<CategorySuggestionPage>;

public sealed record CategorySuggestionPage(IReadOnlyList<CategorySuggestionView> Items, int Total);

/// <summary>
/// A suggestion: the <paramref name="LineCount"/> uncategorised lines of accounts the actor writes whose suggested pattern is
/// <paramref name="Pattern"/>, in <paramref name="Direction"/> (<c>debit</c> or <c>credit</c>), and that no rule matches, would take
/// <paramref name="CategoryId"/>: <paramref name="EvidenceCount"/> lines of accounts they read, categorised by a person, are there, out of
/// <paramref name="Agreement"/> % of that pattern's. <paramref name="Lines"/> are the most recent covered lines, as many as one bulk categorisation takes.
/// </summary>
public sealed record CategorySuggestionView(
    string Pattern, string Direction, Guid CategoryId, string CategoryName, int LineCount, int EvidenceCount, int Agreement,
    IReadOnlyList<BankTransactionView> Lines);

/// <summary>The dismissals the actor made, or whose pattern a line they read has (a dismissal names a pattern, which may be a label's).</summary>
public sealed record ListSuggestionDismissalsQuery : IQuery<IReadOnlyList<SuggestionDismissalView>>;

/// <summary>« Ignorer » a suggestion, for the whole household: <paramref name="Pattern"/>, of <paramref name="Direction"/>, never again into the category.</summary>
public sealed record DismissCategorySuggestionCommand(string Pattern, string? Direction, Guid CategoryId) : ICommand<SuggestionDismissalView>;

/// <summary>Brings a dismissed suggestion back: one the actor sees in <see cref="ListSuggestionDismissalsQuery"/>.</summary>
public sealed record WithdrawSuggestionDismissalCommand(Guid Id) : ICommand<Guid>;

public sealed record SuggestionDismissalView(Guid Id, string Pattern, string Direction, Guid CategoryId, string CategoryName, Instant CreatedAt);

[RequestHandler<ListCategorySuggestionsQuery, CategorySuggestionPage>]
public sealed class ListCategorySuggestionsHandler(
    BankingAccess access, IAccountStore accounts, ICategoryStore categories, ICategorySuggestionStore suggestions, BankLines lines)
    : IRequestHandler<ListCategorySuggestionsQuery, CategorySuggestionPage>
{
    public const int PageSize = 10;

    public async ValueTask<Result<CategorySuggestionPage>> HandleAsync(ListCategorySuggestionsQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<CategorySuggestionPage>(enabled);
        var rights = await access.ReadableAsync(await accounts.AllAsync(ct), ct);
        var writable = rights.Where(r => r.CanWrite).Select(r => r.Account.Id).ToList();
        if (writable.Count == 0)
            return Result.Success(new CategorySuggestionPage([], 0));

        var all = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        // Evidence from every account the actor reads, lines only from those they write: a group never counts an account they cannot read.
        var picks = CategorySuggestions.Decide(await suggestions.EvidenceAsync([.. rights.Select(r => r.Account.Id)], ct),
            id => all.TryGetValue(id, out var category) && category.ArchivedAt is null);
        var open = (await suggestions.OpenAsync(writable, ct)).ToDictionary(o => (o.Pattern, o.Debit), o => o.Lines);
        var dismissed = (await suggestions.DismissalsAsync(ct)).Select(d => (d.Pattern, d.Direction == RuleDirection.Debit, d.Category)).ToHashSet();
        var groups = picks.Where(p => open.ContainsKey((p.Pattern, p.Debit)) && !dismissed.Contains((p.Pattern, p.Debit, p.Category)))
            .Select(p => (Pick: p, Count: open[(p.Pattern, p.Debit)]))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Pick.Pattern, StringComparer.Ordinal).ThenByDescending(g => g.Pick.Debit)
            .ToList();

        var readable = rights.ToDictionary(r => r.Account.Id, r => r.Account);
        var items = new List<CategorySuggestionView>();
        foreach (var (pick, count) in groups.Skip((Math.Clamp(query.Page, 1, BankLines.MaxPage) - 1) * PageSize).Take(PageSize))
        {
            var covered = await suggestions.OpenLinesAsync(writable, pick.Pattern, pick.Debit, SetTransactionsCategoryHandler.Most, ct);
            items.Add(new CategorySuggestionView(pick.Pattern, pick.Debit ? "debit" : "credit", pick.Category, all[pick.Category].Name, count,
                pick.Lines, pick.Agreement, await lines.ViewsAsync(covered, readable, ct)));
        }
        return Result.Success(new CategorySuggestionPage(items, groups.Count));
    }
}

[RequestHandler<ListSuggestionDismissalsQuery, IReadOnlyList<SuggestionDismissalView>>]
public sealed class ListSuggestionDismissalsHandler(BankingAccess access, IAccountStore accounts, ICategoryStore categories, ICategorySuggestionStore suggestions)
    : IRequestHandler<ListSuggestionDismissalsQuery, IReadOnlyList<SuggestionDismissalView>>
{
    public async ValueTask<Result<IReadOnlyList<SuggestionDismissalView>>> HandleAsync(ListSuggestionDismissalsQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<IReadOnlyList<SuggestionDismissalView>>(enabled);
        var names = (await categories.AllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        return Result.Success<IReadOnlyList<SuggestionDismissalView>>(
            [.. (await VisibleAsync(access, accounts, suggestions, ct)).Select(d => View(d, names[d.Category]))]);
    }

    internal static async Task<IReadOnlyList<SuggestionDismissal>> VisibleAsync(
        BankingAccess access, IAccountStore accounts, ICategorySuggestionStore suggestions, CancellationToken ct)
        => await suggestions.DismissalsAsync(ct, [.. (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Select(r => r.Account.Id)], access.Person);

    internal static SuggestionDismissalView View(SuggestionDismissal d, string category)
        => new(d.Id, d.Pattern, BankLines.Wire(d.Direction), d.Category, category, d.CreatedAt);
}

[RequestHandler<DismissCategorySuggestionCommand, SuggestionDismissalView>]
public sealed class DismissCategorySuggestionHandler(BankingAccess access, ICategoryStore categories, ICategorySuggestionStore suggestions)
    : IRequestHandler<DismissCategorySuggestionCommand, SuggestionDismissalView>
{
    public async ValueTask<Result<SuggestionDismissalView>> HandleAsync(DismissCategorySuggestionCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<SuggestionDismissalView>(enabled);
        var normalized = CategoryRules.NormalizePattern(command.Pattern);
        if (!normalized.TryGetValue(out var pattern) && normalized.TryGetFailure(out var refused))
            return Result.Failure<SuggestionDismissalView>(FieldErrors.At(refused!, "pattern"));
        if (command.Direction is not ("debit" or "credit"))
            return Result.Failure<SuggestionDismissalView>(FieldErrors.Shape([new FieldError("direction", FieldErrors.ShapeCode, "A direction is debit or credit.")]));
        var inUse = await SetTransactionCategoryHandler.InUseAsync(categories, command.CategoryId, ct);
        if (!inUse.IsSuccess)
            return BankingAccess.Failed<SuggestionDismissalView>(inUse);

        var dismissal = await suggestions.DismissAsync(pattern!, command.Direction == "debit" ? RuleDirection.Debit : RuleDirection.Credit,
            command.CategoryId, access.Person, ct);
        return Result.Success(ListSuggestionDismissalsHandler.View(dismissal, (await categories.FindAsync(command.CategoryId, ct))!.Name));
    }
}

[RequestHandler<WithdrawSuggestionDismissalCommand, Guid>]
public sealed class WithdrawSuggestionDismissalHandler(BankingAccess access, IAccountStore accounts, ICategorySuggestionStore suggestions)
    : IRequestHandler<WithdrawSuggestionDismissalCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(WithdrawSuggestionDismissalCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<Guid>(enabled);
        return (await ListSuggestionDismissalsHandler.VisibleAsync(access, accounts, suggestions, ct)).Any(d => d.Id == command.Id)
            && await suggestions.WithdrawAsync(command.Id, ct)
            ? Result.Success(command.Id)
            : Result.Failure<Guid>(new NotFoundFailure("suggestion dismissal", command.Id.ToString()));
    }
}
