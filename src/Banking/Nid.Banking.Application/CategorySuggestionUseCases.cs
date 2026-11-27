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
/// <paramref name="Basis"/> (#450): <c>history</c>, the pattern's own lines; <c>meaning</c>, those of patterns near it by meaning;
/// <c>category_name</c>, no lines at all: the pattern is near the category's name, <paramref name="Agreement"/> being that similarity in percent;
/// <c>ai</c>, a model's guess, <paramref name="Agreement"/> being its probability in percent (0 for a model with no scores), on a worker of that
/// <paramref name="Placement"/> (<c>local</c>, <c>remote</c>, or <c>cloud</c> for a cloud provider, #812).
/// </summary>
public sealed record CategorySuggestionView(
    string Pattern, string Direction, Guid CategoryId, string CategoryName, int LineCount, int EvidenceCount, int Agreement,
    IReadOnlyList<BankTransactionView> Lines, string Basis, string? Placement = null);

/// <summary>The dismissals the actor made, or whose pattern a line they read has (a dismissal names a pattern, which may be a label's).</summary>
public sealed record ListSuggestionDismissalsQuery : IQuery<IReadOnlyList<SuggestionDismissalView>>, IRequiresBanking;

/// <summary>« Ignorer » a suggestion, for the whole household: <paramref name="Pattern"/>, of <paramref name="Direction"/>, never again into the category.</summary>
public sealed record DismissCategorySuggestionCommand(string Pattern, string? Direction, Guid CategoryId) : ICommand<SuggestionDismissalView>, IRequiresBanking;

/// <summary>Brings a dismissed suggestion back: one the actor sees in <see cref="ListSuggestionDismissalsQuery"/>.</summary>
public sealed record WithdrawSuggestionDismissalCommand(Guid Id) : ICommand<Guid>, IRequiresBanking;

public sealed record SuggestionDismissalView(Guid Id, string Pattern, string Direction, Guid CategoryId, string CategoryName, Instant CreatedAt);

/// <summary>
/// #449, #450: the category Nid suggests for each open pattern (lines of accounts the reader writes) and direction: the household's own
/// choices (step A), else the choices on patterns near it by meaning, else the category whose name it is near, else a model's cached guess;
/// never one a dismissal names, which lets the next basis try. Computed on every read, over the reader's lines only (H4). Shared by the
/// « Suggestions » block and the « À classer » tab.
/// </summary>
public sealed class CategoryPicks(
    ICategoryStore categories, ICategorySuggestionStore suggestions, IPatternIndexStore index, IEmbeddings embeddings, DecideSetup setup,
    IDecideCalibrationStore calibrations)
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
        var open = (await suggestions.OpenAsync(writable, ct)).ToDictionary(o => (o.Pattern, o.Debit), o => o.Lines);
        var dismissed = (await suggestions.DismissalsAsync(ct)).Select(d => (d.Pattern, d.Direction == RuleDirection.Debit, d.Category)).ToHashSet();
        bool Shown(CategorySuggestions.Pick p) => open.ContainsKey((p.Pattern, p.Debit)) && !dismissed.Contains((p.Pattern, p.Debit, p.Category));

        // Each basis in turn, for the patterns the ones before leave with nothing to show.
        var picks = CategorySuggestions.Decide(evidence, InUse).Where(Shown).ToList();
        List<(string Pattern, bool Debit)> Left() => [.. open.Keys.Except(picks.Select(p => (p.Pattern, p.Debit)))];
        if (await NeighboursAsync(ct) is { } neighbours)
        {
            if (neighbours.Profile.Threshold is { } threshold && evidence.Count > 0 && Left() is { Count: > 0 } left)
                picks.AddRange((await ByMeaningAsync(neighbours.Model, left, evidence, InUse, threshold, ct)).Where(Shown));
            if (neighbours.Profile.ColdThreshold is { } cold && Left() is { Count: > 0 } coldLeft)
                picks.AddRange((await ByNameAsync(neighbours.Model, coldLeft, all.Values, InUse, cold, ct)).Where(Shown));
        }
        if (Left() is { Count: > 0 } unguessed)
            picks.AddRange((await GuessedAsync(unguessed, InUse, ct)).Where(Shown));
        return new Found(picks, open, all);
    }

    /// <summary>#450: the embed model and its nearest-neighbours profile (<c>docs/ai/README.md</c> §7.3), while the <c>decide</c> use is on.</summary>
    private async Task<(string Model, DecideProfile Profile)?> NeighboursAsync(CancellationToken ct)
        => await embeddings.UseOnAsync(ModelUses.Decide, ct) && (await setup.NeighboursAsync(ct)).Key is { } key
            && await calibrations.ProfileAsync(key, ct) is { } profile
            ? (key.Model, profile)
            : null;

    /// <summary>#450: the evidence of patterns near the undecided ones by meaning, at the profile's threshold. Vectors are made in the
    /// background; the vote counts the reader's lines only (H4), on every read.</summary>
    private async Task<IEnumerable<CategorySuggestions.Pick>> ByMeaningAsync(string model, IReadOnlyList<(string Pattern, bool Debit)> undecided,
        IReadOnlyList<CategorySuggestions.Evidence> evidence, Func<Guid, bool> inUse, double threshold, CancellationToken ct)
    {
        var neighbours = await index.NeighboursAsync(model, [.. undecided.Select(u => u.Pattern).Distinct()],
            [.. evidence.Select(e => e.Pattern).Distinct()], CategorySuggestions.Neighbours, ct);
        return undecided.Select(u => CategorySuggestions.ByMeaning(u.Pattern, u.Debit, neighbours, evidence, inUse, threshold)).OfType<CategorySuggestions.Pick>();
    }

    /// <summary>#450, cold start: the category of its side whose name the pattern is nearest, at the profile's cold threshold.</summary>
    private async Task<IEnumerable<CategorySuggestions.Pick>> ByNameAsync(string model, IReadOnlyList<(string Pattern, bool Debit)> undecided,
        IEnumerable<BankCategory> all, Func<Guid, bool> inUse, double threshold, CancellationToken ct)
    {
        Guid[] Side(CategoryKind kind) => [.. all.Where(c => c.Kind == kind && inUse(c.Id)).Select(c => c.Id)];
        var near = await index.NearestCategoriesAsync(model, [.. undecided.Select(c => c.Pattern).Distinct()], Side(CategoryKind.Expense),
            Side(CategoryKind.Income), ct);
        return undecided.Select(u => CategorySuggestions.ByName(u.Pattern, u.Debit, near, threshold)).OfType<CategorySuggestions.Pick>();
    }

    /// <summary>#450: the <c>decide</c> model's guesses (<see cref="CategoryGuesser"/>), cached for the household, shown at the profile's
    /// probability (any, for a model with no scores) on a category still in use.</summary>
    private async Task<IEnumerable<CategorySuggestions.Pick>> GuessedAsync(IReadOnlyList<(string Pattern, bool Debit)> undecided, Func<Guid, bool> inUse,
        CancellationToken ct)
    {
        if (await setup.WorkerProfileAsync(calibrations, ct) is not { } worker)
            return [];
        var guesses = (await suggestions.GuessesAsync(worker.Key, ct)).ToDictionary(g => (g.Pattern, g.Debit));
        return undecided.Select(u => guesses.GetValueOrDefault(u)).OfType<CategoryGuess>()
            .Where(g => g.Category is { } category && inUse(category) && (worker.Profile.Scoreless || g.Probability >= worker.Profile.Threshold))
            .Select(g => new CategorySuggestions.Pick(g.Pattern, g.Debit, g.Category!.Value, 0, 0, SuggestionBases.Ai, (int)Math.Round((g.Probability ?? 0) * 100),
                g.Placement));
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
                pick.Lines, pick.Agreement, await lines.ViewsAsync(covered, readable, ct), pick.Basis, pick.Placement));
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
