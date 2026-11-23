using System.Globalization;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using Readable = System.Collections.Generic.IReadOnlyDictionary<System.Guid, (Nid.Banking.Contracts.BankAccount Account, bool CanWrite)>;

namespace Nid.Banking.Application;

/// <summary>Decision 7: the unrestricted rules and those restricted to an account the actor reads, oldest first.</summary>
public sealed record ListCategoryRulesQuery : IQuery<IReadOnlyList<CategoryRuleView>>, IRequiresBanking;

/// <summary>
/// <paramref name="Direction"/> is <c>debit</c>, <c>credit</c> or null for both; the bounds are absolute amounts, inclusive, as strings.
/// With <paramref name="AccountId"/>, the actor writes that account.
/// </summary>
public sealed record CreateCategoryRuleCommand(
    string Pattern, Guid? AccountId, string? Direction, string? MinAmount, string? MaxAmount, Guid CategoryId) : ICommand<CategoryRuleView>, IRequiresBanking;

/// <summary>
/// The rule as a whole, as on creation: a field left out is cleared. Its author, or an adult who writes its account; only the author
/// changes its account.
/// </summary>
public sealed record UpdateCategoryRuleCommand(
    Guid Id, string Pattern, Guid? AccountId, string? Direction, string? MinAmount, string? MaxAmount, Guid CategoryId) : ICommand<CategoryRuleView>, IRequiresBanking;

/// <summary>Its author, or an adult who writes its account; the lines it categorised keep their category.</summary>
public sealed record DeleteCategoryRuleCommand(Guid Id) : ICommand<Guid>, IRequiresBanking;

/// <summary>The lines « Appliquer aux opérations passées » would change, newest first, 50 a page (<paramref name="Page"/> counts from 1).</summary>
public sealed record PreviewCategoryRuleQuery(Guid Id, int Page) : IQuery<CategoryRulePreview>, IRequiresBanking;

/// <summary>The preview's lines, every page, take the rule's category; how many changed.</summary>
public sealed record ApplyCategoryRuleCommand(Guid Id) : ICommand<int>, ITransactional, IRequiresBanking;

public sealed record CategoryRulePreview(int Total, IReadOnlyList<BankTransactionView> Items);

/// <summary>
/// #418: what a rule not yet saved would do, as <see cref="PreviewCategoryRuleQuery"/> once created, without saving anything; the fields
/// and the rights of <see cref="CreateCategoryRuleCommand"/>.
/// </summary>
public sealed record PreviewCategoryRuleDraftQuery(
    string Pattern, Guid? AccountId, string? Direction, string? MinAmount, string? MaxAmount, Guid CategoryId) : IQuery<CategoryRuleDraftPreview>, IRequiresBanking;

/// <summary>
/// <paramref name="Total"/> lines it would change, the most recent of them in <paramref name="Items"/>; <paramref name="Conflicts"/>, the
/// lines it matches in another category but would not change; <paramref name="TooBroad"/>: the lines it matches sit in several other categories.
/// </summary>
public sealed record CategoryRuleDraftPreview(int Total, IReadOnlyList<BankTransactionView> Items, IReadOnlyList<CategoryRuleConflict> Conflicts, bool TooBroad);

/// <summary>
/// <paramref name="Reason"/>: <c>hand</c> (classified by a person, the assistant or a split), <c>rule</c> (another rule wins them,
/// <paramref name="RulePattern"/>, and gives them <paramref name="CategoryId"/>) or <c>link</c> (categorised through their link).
/// A null category is « Sans catégorie ».
/// </summary>
public sealed record CategoryRuleConflict(string Reason, string? RulePattern, Guid? CategoryId, string? CategoryName, int Lines);

/// <summary>
/// <paramref name="CanEdit"/>: the actor is its author or writes its account. <paramref name="Lines"/> and <paramref name="LastUsedAt"/>: the
/// lines it categorised that the actor reads, and the latest time it categorised one of them.
/// </summary>
public sealed record CategoryRuleView(
    Guid Id, string Pattern, Guid? AccountId, string? AccountName, string? Direction, string? MinAmount, string? MaxAmount, Guid CategoryId,
    string CategoryName, bool CanEdit, int Lines, Instant? LastUsedAt, bool CategoryArchived);

/// <summary>Spec §Rules, decision 7: who sees and edits a rule, what a rule says, and the lines it would change for the actor.</summary>
public sealed class CategoryRuleScope(BankingAccess access, IAccountStore accounts, ICategoryRuleStore rules, ICategoryStore categories)
{
    public async Task<Readable> ReadableAsync(CancellationToken ct)
        => (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).ToDictionary(r => r.Account.Id);

    public static bool Sees(CategoryRule rule, Readable readable) => rule.Account is not { } only || readable.ContainsKey(only);

    public bool Edits(CategoryRule rule, Readable readable)
        => rule.CreatedBy == access.Person || (rule.Account is { } only && readable.TryGetValue(only, out var account) && account.CanWrite);

    /// <summary>The rule when the actor sees it, and edits it when <paramref name="edit"/>: « not found » unseen, forbidden only seen.</summary>
    public async Task<Result<CategoryRule>> FindAsync(Guid id, bool edit, Readable readable, CancellationToken ct)
    {
        if (await rules.FindAsync(id, ct) is not { } rule || !Sees(rule, readable))
            return Result.Failure<CategoryRule>(new NotFoundFailure("category rule", id.ToString()));
        return !edit || Edits(rule, readable)
            ? Result.Success(rule)
            : Result.Failure<CategoryRule>(new UnauthorizedFailure("Only its author, or who writes its account, changes this rule.", null));
    }

    /// <summary>The rule a request describes, by the actor, under <paramref name="id"/>.</summary>
    public async Task<Result<CategoryRule>> DraftAsync(
        Guid id, string? pattern, Guid? account, string? direction, string? min, string? max, Guid category, CancellationToken ct)
    {
        var normalized = CategoryRules.NormalizePattern(pattern ?? "");
        if (!normalized.TryGetValue(out var text) && normalized.TryGetFailure(out var refused))
            return Result.Failure<CategoryRule>(FieldErrors.At(refused!, "pattern"));
        if (direction is not (null or "debit" or "credit"))
            return Result.Failure<CategoryRule>(FieldErrors.Shape([new FieldError("direction", FieldErrors.ShapeCode, "A direction is debit, credit or null.")]));
        if (!Bound(min, out var low))
            return Amounts("minAmount");
        if (!Bound(max, out var high))
            return Amounts("maxAmount");
        if (low > high)
            return Amounts("minAmount");
        if (await categories.FindAsync(category, ct) is not { } target)
            return Result.Failure<CategoryRule>(new NotFoundFailure("bank category", category.ToString()));
        // A merged category is archived too: its rules went to its target.
        if (target.ArchivedAt is not null)
            return Result.Failure<CategoryRule>(FieldErrors.At(
                new Failure(BankingCodes.CategoryArchived, "A rule takes only a category in use.", null), "categoryId"));
        if (account is { } only && (await access.AccountAsync(only, AccessKind.Write, ct)).TryGetFailure(out var denied))
            return Result.Failure<CategoryRule>(denied!);
        var way = direction switch { "debit" => RuleDirection.Debit, "credit" => RuleDirection.Credit, _ => (RuleDirection?)null };
        return Result.Success(new CategoryRule(id, text!, account, way, low, high, category, access.Person, default, null));
    }

    // Absolute amounts: no sign is read, so a negative bound is refused with the unreadable ones; numeric(19,4) holds under 10^15.
    private static bool Bound(string? text, out decimal? value)
    {
        value = null;
        if (text is null)
            return true;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed >= 1_000_000_000_000_000m)
            return false;
        value = parsed;
        return true;
    }

    private static Result<CategoryRule> Amounts(string field) => Result.Failure<CategoryRule>(FieldErrors.At(
        new Failure(BankingCodes.RuleAmounts, "A rule's bounds are amounts of zero or more, the minimum not above the maximum.", null), field));

    /// <summary>
    /// The lines the rule would change for the actor (so the preview counts what apply changes): of accounts they write within its
    /// restriction, matched by it, with no category or a rule's or a hint's other than its own, and won by it among the rules they see,
    /// as the pass picks (<see cref="CategoryRuleApplier"/>).
    /// </summary>
    public async Task<IReadOnlyList<BankTransaction>> TargetsAsync(CategoryRule rule, Readable readable, CancellationToken ct)
        => [.. (await MatchedAsync(rule, readable, ct)).Where(m => Changes(rule, m.Line, m.Winner)).Select(m => m.Line)];

    public static bool Changes(CategoryRule rule, BankTransaction line, CategoryRule? winner)
        => winner?.Id == rule.Id && line.Category != rule.Category
           && line.CategorySource is null or CategorySource.Rule or CategorySource.Hint;

    /// <summary>
    /// The lines of accounts the actor writes within the rule's restriction that it matches, newest first, each with the rule that wins it
    /// among those they see. A draft (<see cref="Guid.Empty"/>) competes with them as the newest rule, as it will once created.
    /// </summary>
    public async Task<IReadOnlyList<(BankTransaction Line, CategoryRule? Winner)>> MatchedAsync(CategoryRule rule, Readable readable, CancellationToken ct)
    {
        var writable = readable.Values.Where(r => r.CanWrite && (rule.Account is null || r.Account.Id == rule.Account)).Select(r => r.Account.Id).ToList();
        if (writable.Count == 0)
            return [];
        var archived = (await categories.AllAsync(ct)).Where(c => c.ArchivedAt is not null).Select(c => c.Id).ToHashSet();
        var seen = (await rules.AllAsync(ct)).Where(r => Sees(r, readable) && !archived.Contains(r.Category)).ToList();
        if (rule.Id == Guid.Empty)
            seen.Add(rule);
        return [.. (await rules.CandidatesAsync(rule, writable, ct)).Select(l => (Line: l, Counterparty: l.Counterparty is { } raw ? TransactionLabel.Normalize(raw) : null))
            .Where(l => CategoryRules.Matches(rule, l.Line.Account, l.Line.Amount, l.Line.NormalizedLabel, l.Counterparty))
            .Select(l => (l.Line, CategoryRules.Pick(seen, l.Line.Account, l.Line.Amount, l.Line.NormalizedLabel, l.Counterparty)))];
    }

    public async Task<IReadOnlyList<CategoryRuleView>> ViewsAsync(IReadOnlyList<CategoryRule> list, Readable readable, CancellationToken ct)
    {
        var all = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        // Decision 7: what a rule did on lines the actor cannot read is not theirs to see, neither the count nor the date.
        var usage = await rules.UsageAsync([.. readable.Keys], ct);
        return [.. list.Select(r => new CategoryRuleView(
            r.Id, r.Pattern, r.Account, r.Account is { } a && readable.TryGetValue(a, out var account) ? account.Account.Name : null,
            r.Direction is { } d ? BankLines.Wire(d) : null, r.MinAmount is { } min ? TransactionLinker.Amount(min) : null,
            r.MaxAmount is { } max ? TransactionLinker.Amount(max) : null, r.Category, all.TryGetValue(r.Category, out var category) ? category.Name : "", Edits(r, readable),
            usage.GetValueOrDefault(r.Id).Lines, usage.GetValueOrDefault(r.Id).LastUsed, category?.ArchivedAt is not null))];
    }
}

[RequestHandler<ListCategoryRulesQuery, IReadOnlyList<CategoryRuleView>>]
public sealed class ListCategoryRulesHandler(ICategoryRuleStore rules, CategoryRuleScope scope)
    : IRequestHandler<ListCategoryRulesQuery, IReadOnlyList<CategoryRuleView>>
{
    public async ValueTask<Result<IReadOnlyList<CategoryRuleView>>> HandleAsync(ListCategoryRulesQuery query, CancellationToken ct = default)
    {
        var readable = await scope.ReadableAsync(ct);
        return Result.Success(await scope.ViewsAsync([.. (await rules.AllAsync(ct)).Where(r => CategoryRuleScope.Sees(r, readable))], readable, ct));
    }
}

[RequestHandler<CreateCategoryRuleCommand, CategoryRuleView>]
public sealed class CreateCategoryRuleHandler(ICategoryRuleStore rules, CategoryRuleScope scope)
    : IRequestHandler<CreateCategoryRuleCommand, CategoryRuleView>
{
    public async ValueTask<Result<CategoryRuleView>> HandleAsync(CreateCategoryRuleCommand command, CancellationToken ct = default)
    {
        var draft = await scope.DraftAsync(
            Guid.Empty, command.Pattern, command.AccountId, command.Direction, command.MinAmount, command.MaxAmount, command.CategoryId, ct);
        if (!draft.TryGetValue(out var rule))
            return BankingAccess.Failed<CategoryRuleView, CategoryRule>(draft);
        var created = await rules.CreateAsync(rule!, ct);
        return Result.Success((await scope.ViewsAsync([created], await scope.ReadableAsync(ct), ct))[0]);
    }
}

[RequestHandler<UpdateCategoryRuleCommand, CategoryRuleView>]
public sealed class UpdateCategoryRuleHandler(BankingAccess access, ICategoryRuleStore rules, CategoryRuleScope scope)
    : IRequestHandler<UpdateCategoryRuleCommand, CategoryRuleView>
{
    public async ValueTask<Result<CategoryRuleView>> HandleAsync(UpdateCategoryRuleCommand command, CancellationToken ct = default)
    {
        var readable = await scope.ReadableAsync(ct);
        var found = await scope.FindAsync(command.Id, edit: true, readable, ct);
        if (!found.TryGetValue(out var current))
            return BankingAccess.Failed<CategoryRuleView, CategoryRule>(found);
        // Who may edit follows the restriction: only the author moves it.
        if (command.AccountId != current!.Account && current.CreatedBy != access.Person)
            return Result.Failure<CategoryRuleView>(new UnauthorizedFailure("Only its author changes the account of this rule.", null));
        var draft = await scope.DraftAsync(
            command.Id, command.Pattern, command.AccountId, command.Direction, command.MinAmount, command.MaxAmount, command.CategoryId, ct);
        if (!draft.TryGetValue(out var rule))
            return BankingAccess.Failed<CategoryRuleView, CategoryRule>(draft);
        // Last write wins: a pass touching last_used_at in between never refuses an edit.
        return await rules.UpdateAsync(rule!, ct) is { } updated
            ? Result.Success((await scope.ViewsAsync([updated], readable, ct))[0])
            : Result.Failure<CategoryRuleView>(new NotFoundFailure("category rule", command.Id.ToString()));
    }
}

[RequestHandler<DeleteCategoryRuleCommand, Guid>]
public sealed class DeleteCategoryRuleHandler(ICategoryRuleStore rules, CategoryRuleScope scope)
    : IRequestHandler<DeleteCategoryRuleCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteCategoryRuleCommand command, CancellationToken ct = default)
    {
        var found = await scope.FindAsync(command.Id, edit: true, await scope.ReadableAsync(ct), ct);
        if (!found.IsSuccess)
            return BankingAccess.Failed<Guid, CategoryRule>(found);
        return await rules.DeleteAsync(command.Id, ct)
            ? Result.Success(command.Id)
            : Result.Failure<Guid>(new NotFoundFailure("category rule", command.Id.ToString()));
    }
}

[RequestHandler<PreviewCategoryRuleQuery, CategoryRulePreview>]
public sealed class PreviewCategoryRuleHandler(CategoryRuleScope scope, BankLines lines)
    : IRequestHandler<PreviewCategoryRuleQuery, CategoryRulePreview>
{
    public async ValueTask<Result<CategoryRulePreview>> HandleAsync(PreviewCategoryRuleQuery query, CancellationToken ct = default)
    {
        var readable = await scope.ReadableAsync(ct);
        var found = await scope.FindAsync(query.Id, edit: false, readable, ct);
        if (!found.TryGetValue(out var rule))
            return BankingAccess.Failed<CategoryRulePreview, CategoryRule>(found);

        var targets = await scope.TargetsAsync(rule!, readable, ct);
        var page = targets.Skip((Math.Clamp(query.Page, 1, BankLines.MaxPage) - 1) * BankLines.PageSize).Take(BankLines.PageSize).ToList();
        return Result.Success(new CategoryRulePreview(targets.Count, await lines.ViewsAsync(page, ct)));
    }
}

[RequestHandler<PreviewCategoryRuleDraftQuery, CategoryRuleDraftPreview>]
public sealed class PreviewCategoryRuleDraftHandler(CategoryRuleScope scope, ICategoryStore categories, BankLines lines, IClock clock)
    : IRequestHandler<PreviewCategoryRuleDraftQuery, CategoryRuleDraftPreview>
{
    /// <summary>How many of the lines it would change the dialog lists.</summary>
    public const int Listed = 5;

    /// <summary>Matched lines in this many other categories make a pattern too broad (« CARTE », « VIR »).</summary>
    public const int Broad = 2;

    public async ValueTask<Result<CategoryRuleDraftPreview>> HandleAsync(PreviewCategoryRuleDraftQuery query, CancellationToken ct = default)
    {
        var draft = await scope.DraftAsync(
            Guid.Empty, query.Pattern, query.AccountId, query.Direction, query.MinAmount, query.MaxAmount, query.CategoryId, ct);
        if (!draft.TryGetValue(out var rule))
            return BankingAccess.Failed<CategoryRuleDraftPreview, CategoryRule>(draft);
        rule = rule! with { CreatedAt = clock.GetCurrentInstant() };

        var matched = await scope.MatchedAsync(rule, await scope.ReadableAsync(ct), ct);
        var targets = matched.Where(m => CategoryRuleScope.Changes(rule, m.Line, m.Winner)).Select(m => m.Line).ToList();
        var names = (await categories.AllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var conflicts = matched
            .Where(m => m.Line.Category != rule.Category && !CategoryRuleScope.Changes(rule, m.Line, m.Winner))
            .Select(m => m.Line.CategorySource switch
            {
                CategorySource.Link => (Reason: "link", Rule: (CategoryRule?)null, Category: m.Line.Category),
                CategorySource.Hand or CategorySource.Assistant or CategorySource.Split => (Reason: "hand", Rule: null, Category: m.Line.Category),
                _ => (Reason: "rule", Rule: m.Winner, Category: m.Winner?.Category),
            })
            // A saved rule whose category is archived wins nothing, so nothing beats it: nothing to show.
            .Where(c => c.Reason != "rule" || c.Rule is not null)
            .GroupBy(c => (c.Reason, Rule: c.Rule?.Id, c.Category))
            .Select(g => new CategoryRuleConflict(g.Key.Reason, g.First().Rule?.Pattern, g.Key.Category,
                g.Key.Category is { } id ? names.GetValueOrDefault(id) : null, g.Count()))
            .OrderByDescending(c => c.Lines).ThenBy(c => c.Reason, StringComparer.Ordinal).ThenBy(c => c.CategoryName, StringComparer.Ordinal)
            .ToList();
        var others = matched.Select(m => m.Line.Category).Where(c => c is not null && c != rule.Category).Distinct().Count();
        return Result.Success(new CategoryRuleDraftPreview(targets.Count, await lines.ViewsAsync([.. targets.Take(Listed)], ct), conflicts, others >= Broad));
    }
}

[RequestHandler<ApplyCategoryRuleCommand, int>]
public sealed class ApplyCategoryRuleHandler(CategoryRuleScope scope, ICategoryRuleStore rules, ICategoryStore categories, IClock clock)
    : IRequestHandler<ApplyCategoryRuleCommand, int>
{
    public async ValueTask<Result<int>> HandleAsync(ApplyCategoryRuleCommand command, CancellationToken ct = default)
    {
        var readable = await scope.ReadableAsync(ct);
        var found = await scope.FindAsync(command.Id, edit: false, readable, ct);
        if (!found.TryGetValue(out var rule))
            return BankingAccess.Failed<int, CategoryRule>(found);

        var targets = await scope.TargetsAsync(rule!, readable, ct);
        if (targets.Count == 0)
            return Result.Success(0);
        var now = clock.GetCurrentInstant();
        var changed = await categories.SetForLinesAsync(
            [.. targets.Select(t => t.Id)], rule!.Category, CategorySource.Rule, $"rule:{rule.Id:D}", now, byPerson: false, ct, rule.Id);
        if (changed > 0)
            await rules.TouchAsync([rule.Id], now, ct);
        return Result.Success(changed);
    }
}
