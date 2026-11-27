using Nid.Banking.Application.Operations;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// « À classer »: one page (<see cref="BankLines.PageSize"/>, newest booked first, <paramref name="Page"/> from 1) of the lines with no category
/// that a spending figure counts, on the accounts the actor writes (only those, or <paramref name="Account"/> among them), each with the category
/// Nid suggests (<see cref="CategoryPicks"/>) when it has one.
/// </summary>
public sealed record ListLinesToClassifyQuery(Guid? Account, int Page) : IQuery<LinesToClassifyPage>, IRequiresBanking;

/// <summary><paramref name="Total"/> counts every line to classify the filter keeps; <paramref name="Accounts"/> are those it can pick from.</summary>
public sealed record LinesToClassifyPage(IReadOnlyList<LineToClassifyView> Items, int Total, IReadOnlyList<OptionView> Accounts);

/// <summary>A line to classify and, when Nid has one, its suggestion.</summary>
public sealed record LineToClassifyView(BankTransactionView Line, LineSuggestionView? Suggestion);

/// <summary>
/// The category suggested for a line, as <see cref="CategorySuggestionView"/> says it for its pattern: <paramref name="EvidenceCount"/> lines
/// categorised by a person back it, <paramref name="Agreement"/> % of them agree; <paramref name="Basis"/> what they are (#450: <c>history</c>,
/// <c>meaning</c>, <c>category_name</c>). <paramref name="Pattern"/> is what « Ignorer » dismisses.
/// </summary>
public sealed record LineSuggestionView(Guid CategoryId, string CategoryName, int EvidenceCount, int Agreement, string Basis, string Pattern);

[RequestHandler<ListLinesToClassifyQuery, LinesToClassifyPage>]
public sealed class ListLinesToClassifyHandler(
    BankingAccess access, IAccountStore accounts, ITransactionStore transactions, BankLines lines, CategoryPicks picker)
    : IRequestHandler<ListLinesToClassifyQuery, LinesToClassifyPage>
{
    public async ValueTask<Result<LinesToClassifyPage>> HandleAsync(ListLinesToClassifyQuery query, CancellationToken ct = default)
    {
        var rights = await access.ReadableAsync(await accounts.AllAsync(ct), ct);
        var writable = rights.Where(r => r.CanWrite).Select(r => r.Account).ToList();
        var options = writable.Select(a => new OptionView(a.Id, a.Name)).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // An account they cannot write is no filter: nothing.
        var scope = writable.Where(a => query.Account is not { } only || a.Id == only).Select(a => a.Id).ToList();
        if (scope.Count == 0)
            return Result.Success(new LinesToClassifyPage([], 0, options));

        var readable = rights.Select(r => r.Account).ToList();
        var filter = new OperationFilter(scope, [], null, null, [], true, "counted", null, null, null, null);
        var (items, total, _) = await transactions.OperationsAsync([.. readable.Select(a => a.Id)], filter, [],
            (Math.Clamp(query.Page, 1, BankLines.MaxPage) - 1) * BankLines.PageSize, BankLines.PageSize, ct);

        var found = await picker.ForAsync([.. readable.Select(a => a.Id)], [.. writable.Select(a => a.Id)], ct);
        var picks = found.Picks.ToDictionary(p => (p.Pattern, p.Debit));
        LineSuggestionView? Suggestion(BankTransaction line)
            // A line someone left « Sans catégorie » on purpose, or a split one, is not suggested anything.
            => line.Category is null && line.CategorySource is null && CategoryRules.Suggest(line.NormalizedLabel) is { } pattern
               && picks.TryGetValue((pattern, line.Amount < 0), out var pick)
                ? new LineSuggestionView(pick.Category, found.Categories[pick.Category].Name, pick.Lines, pick.Agreement, pick.Basis, pick.Pattern)
                : null;

        var views = await lines.ViewsAsync(items, readable.ToDictionary(a => a.Id), ct);
        return Result.Success(new LinesToClassifyPage([.. items.Zip(views, (line, view) => new LineToClassifyView(view, Suggestion(line)))], total, options));
    }
}
