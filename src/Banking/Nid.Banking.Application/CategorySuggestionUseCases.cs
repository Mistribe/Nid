using Nid.Ai.Contracts;
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
public sealed record ListCategorySuggestionsQuery(int Page) : IQuery<CategorySuggestionPage>, IRequiresBanking;

public sealed record CategorySuggestionPage(IReadOnlyList<CategorySuggestionView> Items, int Total);

/// <summary>
/// A suggestion: the <paramref name="LineCount"/> uncategorised lines of accounts the actor writes whose suggested pattern is
/// <paramref name="Pattern"/>, in <paramref name="Direction"/> (<c>debit</c> or <c>credit</c>), and that no rule matches, would take
/// <paramref name="CategoryId"/>: <paramref name="EvidenceCount"/> lines of accounts they read, categorised by a person, are there, out of
/// <paramref name="Agreement"/> % of that pattern's. <paramref name="Lines"/> are the most recent covered lines, as many as one bulk categorisation takes.
/// <paramref name="ByMeaning"/> (#450): the evidence is that of patterns near this one by meaning, the pattern having none of its own.
/// </summary>
public sealed record CategorySuggestionView(
    string Pattern, string Direction, Guid CategoryId, string CategoryName, int LineCount, int EvidenceCount, int Agreement,
    IReadOnlyList<BankTransactionView> Lines, bool ByMeaning);

/// <summary>The dismissals the actor made, or whose pattern a line they read has (a dismissal names a pattern, which may be a label's).</summary>
public sealed record ListSuggestionDismissalsQuery : IQuery<IReadOnlyList<SuggestionDismissalView>>, IRequiresBanking;

/// <summary>« Ignorer » a suggestion, for the whole household: <paramref name="Pattern"/>, of <paramref name="Direction"/>, never again into the category.</summary>
public sealed record DismissCategorySuggestionCommand(string Pattern, string? Direction, Guid CategoryId) : ICommand<SuggestionDismissalView>, IRequiresBanking;

/// <summary>Brings a dismissed suggestion back: one the actor sees in <see cref="ListSuggestionDismissalsQuery"/>.</summary>
public sealed record WithdrawSuggestionDismissalCommand(Guid Id) : ICommand<Guid>, IRequiresBanking;

public sealed record SuggestionDismissalView(Guid Id, string Pattern, string Direction, Guid CategoryId, string CategoryName, Instant CreatedAt);

/// <summary>
/// #449, #450: the category Nid suggests for each open pattern (lines of accounts the reader writes) and direction: the household's own
/// choices (step A), else the choices on patterns near it by meaning; never one a dismissal names. Computed on every read, over the reader's
/// lines only (H4). Shared by the « Suggestions » block and the « À classer » tab.
/// </summary>
public sealed class CategoryPicks(ICategoryStore categories, ICategorySuggestionStore suggestions, IPatternIndexStore index, IEmbeddings embeddings)
{
    /// <summary>The picks for the open patterns, with how many open lines each covers, and the household's categories by id.</summary>
    public sealed record Found(IReadOnlyList<CategorySuggestions.Pick> Picks, IReadOnlyDictionary<(string Pattern, bool Debit), int> Open,
        IReadOnlyDictionary<Guid, BankCategory> Categories);

    public async Task<Found> ForAsync(IReadOnlyCollection<Guid> readable, IReadOnlyCollection<Guid> writable, CancellationToken ct)
    {
        var all = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        // Evidence from every account the actor reads, lines only from those they write: a group never counts an account they cannot read.
        var evidence = await suggestions.EvidenceAsync(readable, ct);
        bool InUse(Guid id) => all.TryGetValue(id, out var category) && category.ArchivedAt is null;
        var picks = CategorySuggestions.Decide(evidence, InUse);
        var open = (await suggestions.OpenAsync(writable, ct)).ToDictionary(o => (o.Pattern, o.Debit), o => o.Lines);
        picks = [.. picks, .. await ByMeaningAsync(open.Keys.Except(picks.Select(p => (p.Pattern, p.Debit))).ToList(), evidence, InUse, ct)];
        var dismissed = (await suggestions.DismissalsAsync(ct)).Select(d => (d.Pattern, d.Direction == RuleDirection.Debit, d.Category)).ToHashSet();
        return new Found([.. picks.Where(p => open.ContainsKey((p.Pattern, p.Debit)) && !dismissed.Contains((p.Pattern, p.Debit, p.Category)))], open, all);
    }

    /// <summary>#450: the open patterns the household's own choices leave undecided, decided by the evidence of patterns near them, while the
    /// <c>decide</c> use is on. Their vectors were made in the background; the vote counts the reader's lines only (H4), on every read.</summary>
    private async Task<IReadOnlyList<CategorySuggestions.Pick>> ByMeaningAsync(IReadOnlyList<(string Pattern, bool Debit)> undecided,
        IReadOnlyList<CategorySuggestions.Evidence> evidence, Func<Guid, bool> inUse, CancellationToken ct)
    {
        if (undecided.Count == 0 || evidence.Count == 0 || !await embeddings.UseOnAsync(ModelUses.Decide, ct) || await embeddings.ModelAsync(ct) is not { } model)
            return [];
        var neighbours = await index.NeighboursAsync(model, [.. undecided.Select(u => u.Pattern).Distinct()],
            [.. evidence.Select(e => e.Pattern).Distinct()], CategorySuggestions.Neighbours, ct);
        return [.. undecided.Select(u => CategorySuggestions.ByMeaning(u.Pattern, u.Debit, neighbours, evidence, inUse)).OfType<CategorySuggestions.Pick>()];
    }
}

[RequestHandler<ListCategorySuggestionsQuery, CategorySuggestionPage>]
public sealed class ListCategorySuggestionsHandler(
    BankingAccess access, IAccountStore accounts, ICategorySuggestionStore suggestions, BankLines lines, CategoryPicks picker)
    : IRequestHandler<ListCategorySuggestionsQuery, CategorySuggestionPage>
{
    public const int PageSize = 10;

    public async ValueTask<Result<CategorySuggestionPage>> HandleAsync(ListCategorySuggestionsQuery query, CancellationToken ct = default)
    {
        var rights = await access.ReadableAsync(await accounts.AllAsync(ct), ct);
        var writable = rights.Where(r => r.CanWrite).Select(r => r.Account.Id).ToList();
        if (writable.Count == 0)
            return Result.Success(new CategorySuggestionPage([], 0));

        var found = await picker.ForAsync([.. rights.Select(r => r.Account.Id)], writable, ct);
        var groups = found.Picks.Select(p => (Pick: p, Count: found.Open[(p.Pattern, p.Debit)]))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Pick.Pattern, StringComparer.Ordinal).ThenByDescending(g => g.Pick.Debit)
            .ToList();

        var readable = rights.ToDictionary(r => r.Account.Id, r => r.Account);
        var items = new List<CategorySuggestionView>();
        foreach (var (pick, count) in groups.Skip((Math.Clamp(query.Page, 1, BankLines.MaxPage) - 1) * PageSize).Take(PageSize))
        {
            var covered = await suggestions.OpenLinesAsync(writable, pick.Pattern, pick.Debit, SetTransactionsCategoryHandler.Most, ct);
            items.Add(new CategorySuggestionView(pick.Pattern, pick.Debit ? "debit" : "credit", pick.Category, found.Categories[pick.Category].Name, count,
                pick.Lines, pick.Agreement, await lines.ViewsAsync(covered, readable, ct), pick.ByMeaning));
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
        return (await ListSuggestionDismissalsHandler.VisibleAsync(access, accounts, suggestions, ct)).Any(d => d.Id == command.Id)
            && await suggestions.WithdrawAsync(command.Id, ct)
            ? Result.Success(command.Id)
            : Result.Failure<Guid>(new NotFoundFailure("suggestion dismissal", command.Id.ToString()));
    }
}
