using System.Text.Json;
using Nid.Assets.Contracts;
using Nid.Banking.Application.Operations;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// The booked transactions of an envelope's bank account (<paramref name="ItemId"/> is the envelope) and of the cards attached to it (spec §6), newest first, 50 a page
/// (<paramref name="Page"/> counts from 1); whoever reads the envelope reads them, the pipeline checks it. <paramref name="State"/>: a
/// <c>link_state</c>, or <c>all</c> (the default).
/// </summary>
public sealed record ListBankTransactionsQuery(ItemId ItemId, LocalDate? From, LocalDate? To, int Page, string? State = null) : IQuery<BankTransactionPage>, IItemScoped, IRequiresBanking
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>5C spec §8: the lines of every account the actor reads, cards included, newest first; <paramref name="State"/> defaults to <c>unlinked</c>. <paramref name="Uncategorised"/>: only the lines with no category and no category source that are not ignored.</summary>
public sealed record ListBankLinesQuery(string? State, LocalDate? From, LocalDate? To, int Page, bool Uncategorised = false) : IQuery<BankTransactionPage>, IRequiresBanking;

/// <summary>The charges within ±30 days of a line the actor reads that they may link it to, nearest amount first, 20 at most.</summary>
public sealed record LinkCandidatesQuery(Guid TransactionId) : IQuery<IReadOnlyList<ExpectedChargeView>>, IBankLineScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>The charges the actor reads due in [<paramref name="From"/>, <paramref name="To"/>], 92 days at most.</summary>
public sealed record ListExpectedChargesQuery(LocalDate From, LocalDate To) : IQuery<IReadOnlyList<ExpectedChargeView>>, IRequiresBanking;

/// <summary>A line of an account the actor writes linked by hand to a charge of an item they write, with its rule (<see cref="TransactionLinker.LinkAsync"/>).</summary>
public sealed record LinkTransactionCommand(Guid TransactionId, string Module, Guid ItemId, string? Key, string? Category = null) : ICommand<BankTransactionView>, ITransactional, IBankLineScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A linked, internal or ignored line back to unlinked: write on its account, and on its link's target or read of its counterpart.</summary>
public sealed record UnlinkTransactionCommand(Guid TransactionId) : ICommand<BankTransactionView>, ITransactional, IBankLineScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>An unlinked line no charge is expected for.</summary>
public sealed record IgnoreTransactionCommand(Guid TransactionId) : ICommand<BankTransactionView>, IBankLineScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>A person's category for a line of an account they write; null is « Sans catégorie », kept over any link or rule alike.</summary>
public sealed record SetTransactionCategoryCommand(Guid TransactionId, Guid? CategoryId) : ICommand<BankTransactionView>, ITransactional, IBankLineScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>
/// #426: one category for several lines, each checked as one line is (<see cref="BankLines.CategorisableAsync"/>), in one transaction; a
/// refused line does not stop the others. 1 to <see cref="SetTransactionsCategoryHandler.Most"/> distinct lines.
/// </summary>
public sealed record SetTransactionsCategoryCommand(IReadOnlyList<Guid> TransactionIds, Guid? CategoryId) : ICommand<LinesCategorised>, ITransactional, IRequiresBanking;

/// <summary>
/// #721: the other lines of <paramref name="TransactionId"/>'s normalised label that nothing classified yet (no category, no category source,
/// not ignored), on the accounts the actor writes, newest first; at most <see cref="SameLabelLinesHandler.Most"/>, so they take a category
/// with it in one <see cref="SetTransactionsCategoryCommand"/>.
/// </summary>
public sealed record SameLabelLinesQuery(Guid TransactionId) : IQuery<IReadOnlyList<Guid>>, IBankLineScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>The lines that took the category, and the others with the code one line alone would have been refused with.</summary>
public sealed record LinesCategorised(IReadOnlyList<Guid> Changed, IReadOnlyList<LineRefusal> Refused);

public sealed record LineRefusal(Guid Id, string Code);

/// <summary>« Laisser Nid décider »: the line's category cleared, then its link's again when it is linked, in one transaction.</summary>
public sealed record ResetTransactionCategoryCommand(Guid TransactionId) : ICommand<BankTransactionView>, ITransactional, IBankLineScoped
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>The actor's own rules.</summary>
public sealed record ListBankRulesQuery : IQuery<IReadOnlyList<BankRuleView>>, IRequiresBanking;

public sealed record DeleteBankRuleCommand(Guid Id) : ICommand<Guid>, IRequiresBanking;

public sealed record BankTransactionPage(IReadOnlyList<BankTransactionView> Items, bool HasMore);

/// <summary>
/// <paramref name="Amount"/> is signed (a debit is negative); <paramref name="CardName"/> names the card of a <paramref name="FromCard"/> line,
/// <paramref name="AccountName"/> the line's own account. <paramref name="Link"/> is null when the line is not linked, or its target is
/// hidden from the reader (D19): <paramref name="LinkState"/> still says <c>linked</c> or <c>internal</c>. <paramref name="Category"/> shows to
/// every reader of the line, link masked or not; <paramref name="CategorySource"/> is <c>hand</c>, <c>link</c>…, null when nothing chose it.
/// <paramref name="PartId"/> is set only on a row of the spending lines that stands for one part of a split line (the row's amount, category and note are the part's);
/// a row of those lines that stands for several parts has their sum as <paramref name="Amount"/> and only them as <paramref name="Parts"/>.
/// <paramref name="Note"/> is a person's note; <paramref name="Parts"/> the parts of a split line (<paramref name="CategorySource"/> <c>split</c>), null otherwise.
/// <paramref name="SuggestedPattern"/> is the pattern a rule made from the line would offer (<see cref="CategoryRules.Suggest"/>), null when none fits;
/// <paramref name="SharedAccount"/> says whether every adult reads the line's account (<paramref name="AccountId"/>, a card's own for its lines).
/// <paramref name="DebitedFrom"/> names, on an immediate card's line, the account its echo was debited from (#416), when the reader reads it.
/// </summary>
public sealed record BankTransactionView(
    Guid Id, LocalDate BookedOn, LocalDate? ValueOn, string Amount, string Currency, string Label, string? Counterparty, bool FromCard,
    string? CardName, string FetchedVia, string LinkState, LinkView? Link, string AccountName, CategoryView? Category, string? CategorySource,
    Guid AccountId, string? SuggestedPattern, bool SharedAccount, string? Note = null, IReadOnlyList<PartView>? Parts = null, Guid? PartId = null,
    IReadOnlyList<LinkedDocumentView>? Documents = null, string? DebitedFrom = null);

/// <summary>#84: a part of a split line, <paramref name="Amount"/> signed like the line; the parts add up to it.</summary>
public sealed record PartView(Guid Id, string Amount, CategoryView? Category, string? Note);

/// <summary>A line's category; <paramref name="Kind"/> is <c>expense</c>, <c>income</c> or <c>transfer</c>.</summary>
public sealed record CategoryView(Guid Id, string Name, Guid? ParentId, string? ParentName, string Kind);

/// <summary>A line's link: to a charge of an item (<paramref name="Module"/>, <paramref name="ItemId"/>), or to the counterpart account of an internal one.</summary>
public sealed record LinkView(string Outcome, string? Module, Guid? ItemId, string? ItemName, string? Key, string? CounterpartName,
    string? ExpectedAmount, string? ExpectedCurrency);

/// <summary>A charge (5C spec §6.1); <paramref name="Gap"/> is the line minus the charge, in the line's currency, null when the currencies differ.</summary>
public sealed record ExpectedChargeView(string Module, Guid ItemId, string ItemName, string Kind, string Key, LocalDate DueOn,
    string Amount, string Currency, bool Credit, string? Gap);

/// <summary>
/// One of the actor's rules; <paramref name="AccountName"/> and <paramref name="ItemName"/> are null once they no longer read the account or
/// the item, so they can still delete it.
/// </summary>
public sealed record BankRuleView(Guid Id, string LabelStem, string? AccountName, string Module, Guid ItemId, string? ItemName, Instant CreatedAt,
    Instant? LastUsedAt);

/// <summary>The lines the actor reads, as views masked by D19, and the line a request names.</summary>
public sealed class BankLines(
    BankingAccess access, IAccountStore accounts, ITransactionStore transactions, ILinkStore links, IItemRegistry items, IAccessPolicy policy,
    ICategoryStore categories, ISplitStore splits, IDocumentLinks documents)
{
    public const int PageSize = 50;

    /// <summary>Half a million lines: a page past it would overflow the offset.</summary>
    public const int MaxPage = 10_000;

    /// <summary>
    /// The line, when the actor may <paramref name="kind"/> its account; « not found » when they cannot read it, forbidden when they only
    /// read it.
    /// </summary>
    public async Task<Result<BankTransaction>> FindAsync(Guid id, AccessKind kind, CancellationToken ct)
    {
        if (await transactions.FindAsync(id, ct) is not { } line)
            return NotFound<BankTransaction>(id);
        var account = await access.AccountAsync(line.Account, kind, ct);
        return account.IsSuccess ? Result.Success(line)
            : account.TryGetFailure(out var failure) && failure is UnauthorizedFailure ? Result.Failure<BankTransaction>(failure) : NotFound<BankTransaction>(id);
    }

    public static Result<T> NotFound<T>(Guid line) where T : notnull => Result.Failure<T>(new NotFoundFailure("bank transaction", line.ToString()));

    /// <summary>A line the actor may categorise by hand: one of an account they write, neither split (its parts are) nor ignored (it counts nowhere).</summary>
    public async Task<Result<BankTransaction>> CategorisableAsync(Guid id, CancellationToken ct)
    {
        var found = await FindAsync(id, AccessKind.Write, ct);
        return found.TryGetValue(out var line) ? Categorisable(line!) : found;
    }

    /// <summary>As <see cref="CategorisableAsync"/>, for a line already checked writable.</summary>
    public static Result<BankTransaction> Categorisable(BankTransaction line)
    {
        if (line.CategorySource == CategorySource.Split)
            return Result.Failure<BankTransaction>(SplitUseCases.Split());
        return line.LinkState == TransactionLinkState.Ignored
            ? Result.Failure<BankTransaction>(new ConflictFailure("An ignored line takes no category: un-ignore it first.") { Code = BankingCodes.LineIgnored })
            : Result.Success(line);
    }

    /// <summary>A page of the accounts' lines (null: every account the actor reads) in a <c>link_state</c>, or <c>all</c>.</summary>
    public async Task<BankTransactionPage> PageAsync(IReadOnlyList<Guid>? of, string state, LocalDate? from, LocalDate? to, int page, CancellationToken ct, bool uncategorised = false)
    {
        var readable = await ReadableAsync(ct);
        // One more than a page tells whether another follows.
        var lines = await transactions.ListAsync(of ?? [.. readable.Keys], from, to, (Math.Clamp(page, 1, MaxPage) - 1) * PageSize, PageSize + 1, ct,
            state == "all" ? null : state, [.. readable.Keys], uncategorised);
        return new BankTransactionPage(await ViewsAsync([.. lines.Take(PageSize)], readable, ct), lines.Count > PageSize);
    }

    public async Task<BankTransactionView> ViewAsync(Guid id, CancellationToken ct)
        => (await ViewsAsync([(await transactions.FindAsync(id, ct))!], await ReadableAsync(ct), ct))[0];

    /// <summary>Lines of accounts the actor reads, as they see them.</summary>
    public async Task<IReadOnlyList<BankTransactionView>> ViewsAsync(IReadOnlyList<BankTransaction> lines, CancellationToken ct)
        => await ViewsAsync(lines, await ReadableAsync(ct), ct);

    private async Task<Dictionary<Guid, BankAccount>> ReadableAsync(CancellationToken ct)
        => (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).ToDictionary(r => r.Account.Id, r => r.Account);

    /// <summary>
    /// D19: a link shows only to a reader of its target item, or of its counterpart account; an internal line whose counterpart the reader
    /// cannot read says <c>linked</c>, as the store's state filter counts it.
    /// </summary>
    public async Task<IReadOnlyList<BankTransactionView>> ViewsAsync(
        IReadOnlyList<BankTransaction> lines, IReadOnlyDictionary<Guid, BankAccount> readable, CancellationToken ct)
    {
        var all = (await accounts.AllAsync(ct)).ToDictionary(a => a.Id);
        var found = await links.ForTransactionsAsync([.. lines.Select(l => l.Id)], ct);
        var targets = found.Values.Select(l => l.TargetItem).OfType<ItemId>().Distinct().ToList();
        var names = await items.FindManyAsync([.. await policy.CheckManyAsync(access.Person, targets, AccessKind.Read, ct)], ct);
        var named = (await categories.AllAsync(ct)).ToDictionary(c => c.Id);
        var parts = await splits.PartsAsync([.. lines.Select(l => l.Id)], ct);
        // #84: the receipts the reader reads, as the documents list would show them.
        var receipts = await documents.ReadableOfTargetsAsync(BankingDocumentTargets.Kind, [.. lines.Select(l => l.Id)], access.Person, ct);
        var cardLines = lines.Where(l => all[l.Account].CardOf is not null).Select(l => l.Id).ToList();
        var echoedOn = cardLines.Count == 0 ? new Dictionary<Guid, Guid>() : await links.EchoAccountsAsync(cardLines, ct);
        var adults = await access.AdultCountAsync(ct);
        var shared = new Dictionary<Guid, bool>();
        foreach (var id in lines.Select(l => l.Account).Distinct())
            shared[id] = await access.SharedAsync(all[id], adults, ct);

        // Decision 6: the category shows whatever D19 masks; it names a category, never the link's target.
        CategoryView? Category(Guid? id) => id is { } key && named.TryGetValue(key, out var c)
            ? new CategoryView(c.Id, c.Name, c.Parent, c.Parent is { } parent && named.TryGetValue(parent, out var p) ? p.Name : null, Wire(c.Kind))
            : null;

        LinkView? View(BankLink link) => link switch
        {
            { CounterpartAccount: { } counterpart } when readable.TryGetValue(counterpart, out var other)
                => new LinkView(Wire(link.Outcome), null, null, null, null, other.Name, null, null),
            { TargetItem: { } item } when names.TryGetValue(item, out var target)
                => new LinkView(Wire(link.Outcome), link.TargetModule, item.Value, target.DisplayName, link.TargetKey, null,
                    link.Expected is { } expected ? TransactionLinker.Amount(expected.Amount) : null, link.Expected?.Currency.Code),
            _ => null,
        };

        return [.. lines.Select(t =>
        {
            var account = all[t.Account];
            var view = found.TryGetValue(t.Id, out var link) ? View(link) : null;
            return new BankTransactionView(
                t.Id, t.BookedOn, t.ValueOn, TransactionLinker.Amount(t.Amount), t.Currency.Code, t.Label, t.Counterparty, account.CardOf is not null,
                account.CardOf is not null ? account.Name : null, t.FetchedVia,
                t.LinkState == TransactionLinkState.Internal && view is null ? "linked" : Wire(t.LinkState), view, account.Name,
                Category(t.Category), t.CategorySource is { } source ? Wire(source) : null,
                t.Account, CategoryRules.Suggest(t.NormalizedLabel), shared[t.Account], t.Note,
                parts.TryGetValue(t.Id, out var own) ? [.. own.Select(x => new PartView(x.Id, TransactionLinker.Amount(x.Amount), Category(x.Category), x.Note))] : null,
                Documents: receipts.GetValueOrDefault(t.Id),
                DebitedFrom: echoedOn.TryGetValue(t.Id, out var debited) && readable.TryGetValue(debited, out var from) ? from.Name : null);
        })];
    }

    /// <summary>A <c>state</c> query parameter: a <c>link_state</c> or <c>all</c>, <paramref name="fallback"/> when absent.</summary>
    public static Result<string> State(string? state, string fallback) => (state ?? fallback) switch
    {
        "all" or "unlinked" or "linked" or "ignored" or "internal" => Result.Success(state ?? fallback),
        _ => Result.Failure<string>(FieldErrors.Shape([new FieldError("state", FieldErrors.ShapeCode, "A state is unlinked, linked, ignored, internal or all.")])),
    };

    /// <summary><c>as_expected</c>, <c>loan_instalment</c>, <c>internal</c>…</summary>
    public static string Wire<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

[RequestHandler<ListBankTransactionsQuery, BankTransactionPage>]
public sealed class ListBankTransactionsHandler(IAccountStore accounts, BankLines lines)
    : IRequestHandler<ListBankTransactionsQuery, BankTransactionPage>
{
    public async ValueTask<Result<BankTransactionPage>> HandleAsync(ListBankTransactionsQuery query, CancellationToken ct = default)
    {
        var parsed = BankLines.State(query.State, "all");
        if (!parsed.TryGetValue(out var state))
            return BankingAccess.Failed<BankTransactionPage, string>(parsed);
        if (await accounts.ForEnvelopeAsync(query.ItemId, ct) is not { } account)
            return Result.Success(new BankTransactionPage([], false));

        var cards = (await accounts.AllAsync(ct)).Where(a => a.CardOf == account.Id).Select(a => a.Id);
        return Result.Success(await lines.PageAsync([account.Id, .. cards], state, query.From, query.To, query.Page, ct));
    }
}

[RequestHandler<ListBankLinesQuery, BankTransactionPage>]
public sealed class ListBankLinesHandler(BankLines lines)
    : IRequestHandler<ListBankLinesQuery, BankTransactionPage>
{
    public async ValueTask<Result<BankTransactionPage>> HandleAsync(ListBankLinesQuery query, CancellationToken ct = default)
    {
        var parsed = BankLines.State(query.State, "unlinked");
        if (!parsed.TryGetValue(out var state))
            return BankingAccess.Failed<BankTransactionPage, string>(parsed);

        return Result.Success(await lines.PageAsync(null, state!, query.From, query.To, query.Page, ct, query.Uncategorised));
    }
}

[RequestHandler<LinkCandidatesQuery, IReadOnlyList<ExpectedChargeView>>]
public sealed class LinkCandidatesHandler(IContextAccessor context, BankingAccess access, ExpectedChargeReader charges, IWritableVehicles vehicles)
    : IRequestHandler<LinkCandidatesQuery, IReadOnlyList<ExpectedChargeView>>
{
    public const int Days = 30;
    public const int Most = 20;

    public async ValueTask<Result<IReadOnlyList<ExpectedChargeView>>> HandleAsync(LinkCandidatesQuery query, CancellationToken ct = default)
    {
        var line = CheckedResources.Of<BankTransaction>(context, query);

        var (from, to) = (line!.BookedOn.PlusDays(-Days), line.BookedOn.PlusDays(Days));
        var own = (await charges.WritableAsync(access.Person, from, to, ct)).Where(c => c.Credit == line.Amount > 0).ToList();
        var convert = await charges.ConverterAsync(own, from, to, ct);
        var stem = TransactionLabel.Stem(line.NormalizedLabel);
        var paid = Math.Abs(line.Amount);
        // #649: an expense may pay a running cost of a vehicle the actor writes: no due date or amount of its own, the line's stand in.
        IReadOnlyList<VehicleChoice> costs = line.Amount < 0 ? await vehicles.ForAsync(access.Person, ct) : [];
        return Result.Success<IReadOnlyList<ExpectedChargeView>>([.. own
            .Select(c => (Charge: c, Gap: paid - convert(c, line.BookedOn, line.Currency.Code)))
            // Unknown gaps (no rate) last, then a hint found in the label, then the nearest date.
            .OrderBy(x => x.Gap is null).ThenBy(x => Math.Abs(x.Gap ?? 0))
            .ThenBy(x => !x.Charge.LabelHints.Any(h => TransactionLabel.Normalize(h) is { Length: > 0 } hint && stem.Contains(hint, StringComparison.Ordinal)))
            .ThenBy(x => Math.Abs(Period.DaysBetween(line.BookedOn, x.Charge.DueOn)))
            .Take(Most)
            .Select(x => ExpectedChargeViews.Of(x.Charge, x.Charge.Amount.Currency == line.Currency ? x.Gap : null))
            .Concat(costs.Select(v => new ExpectedChargeView(TransactionLinker.Assets, v.Id.Value, v.Name, "vehicle_cost", "cost", line.BookedOn,
                TransactionLinker.Amount(paid), line.Currency.Code, false, null)))]);
    }
}

internal static class ExpectedChargeViews
{
    public static ExpectedChargeView Of(ExpectedCharge c, decimal? gap) => new(
        c.Module, c.Item.Value, c.ItemName, c.Kind.Name, c.Key, c.DueOn, TransactionLinker.Amount(c.Amount.Amount), c.Amount.Currency.Code,
        c.Credit, gap is { } g ? TransactionLinker.Amount(g) : null);
}

[RequestHandler<ListExpectedChargesQuery, IReadOnlyList<ExpectedChargeView>>]
public sealed class ListExpectedChargesHandler(BankingAccess access, ExpectedChargeReader charges)
    : IRequestHandler<ListExpectedChargesQuery, IReadOnlyList<ExpectedChargeView>>
{
    public const int MaxDays = 92;

    public async ValueTask<Result<IReadOnlyList<ExpectedChargeView>>> HandleAsync(ListExpectedChargesQuery query, CancellationToken ct = default)
    {
        if (query.To < query.From || Period.DaysBetween(query.From, query.To) > MaxDays)
            return Result.Failure<IReadOnlyList<ExpectedChargeView>>(FieldErrors.Shape(
                [new FieldError("to", FieldErrors.ShapeCode, $"A window starts on or before its end and spans {MaxDays} days at most.")]));

        return Result.Success<IReadOnlyList<ExpectedChargeView>>([.. (await charges.ReadableAsync(access.Person, query.From, query.To, ct))
            .OrderBy(c => c.DueOn).ThenBy(c => c.ItemName, StringComparer.Ordinal).Select(c => ExpectedChargeViews.Of(c, null))]);
    }
}

[RequestHandler<LinkTransactionCommand, BankTransactionView>]
public sealed class LinkTransactionHandler(BankingAccess access, BankLines lines, TransactionLinker linker, IHouseholdCalendar calendar)
    : IRequestHandler<LinkTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(LinkTransactionCommand command, CancellationToken ct = default)
    {
        var item = new ItemId(command.ItemId);
        var checkedLine = await linker.CheckAsync(command.TransactionId, command.Module, item, ct);
        if (!checkedLine.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(checkedLine);
        if (command.Module == TransactionLinker.Assets && command.Category is not null && !AssetCostCategories.All.Contains(command.Category))
            return Result.Failure<BankTransactionView>(FieldErrors.Shape(
                [new FieldError("category", FieldErrors.ShapeCode, $"A cost category is one of {string.Join(", ", AssetCostCategories.All)}.")]));

        var linked = await linker.LinkAsync(line!, command.Module, item, command.Key, access.Person, await calendar.TodayAsync(ct), ct, command.Module == TransactionLinker.Assets ? command.Category : null);
        return linked.IsSuccess
            ? Result.Success(await lines.ViewAsync(line!.Id, ct))
            : BankingAccess.Failed<BankTransactionView, BankLink>(linked);
    }
}

[RequestHandler<UnlinkTransactionCommand, BankTransactionView>]
public sealed class UnlinkTransactionHandler(
    IContextAccessor context, BankingAccess access, BankLines lines, ILinkStore links, IAccessPolicy policy, IItemRegistry items, CategoryRuleApplier categoryRules, IFlows flows)
    : IRequestHandler<UnlinkTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(UnlinkTransactionCommand command, CancellationToken ct = default)
    {
        var line = CheckedResources.Of<BankTransaction>(context, command);

        // D19: a link the actor cannot see is not found, never named.
        var link = (await links.ForTransactionsAsync([line!.Id], ct)).GetValueOrDefault(line.Id);
        switch (link)
        {
            case { CounterpartAccount: { } counterpart } when !(await access.AccountAsync(counterpart, AccessKind.Read, ct)).IsSuccess:
                return BankLines.NotFound<BankTransactionView>(line.Id);
            // A target gone from the registry (deleted) asks only for write on the account.
            case { TargetItem: { } item } when await items.FindAsync(item, ct) is not null:
                var write = await policy.CheckAsync(access.Person, item, AccessKind.Write, ct);
                if (write.TryGetFailure(out var failure))
                    return failure is UnauthorizedFailure ? Result.Failure<BankTransactionView>(failure) : BankLines.NotFound<BankTransactionView>(line.Id);
                break;
        }

        // #649: the cost a vehicle link created (its flow carries this line's `bank:` reference) goes with the link; a hand-typed cost the link
        // reused stays. Same transaction: a refusal undoes the unlink.
        // A correction supersedes the flow (new id, same `bank:` reference) while the link keeps the old id: find the current one by reference.
        if (link is { TargetModule: "assets", TargetItem: { } vehicle }
            && DateRange.Create(new LocalDate(1900, 1, 1), new LocalDate(2200, 1, 1)).TryGetValue(out var all)
            && (await flows.ListAsync(vehicle, all, new HashSet<FlowKind> { FlowKind.Expense }, ct)).TryGetValue(out var costs)
            && costs!.FirstOrDefault(f => f.ExternalRef == $"bank:{line.Id:D}") is { } cost)
        {
            var voided = await flows.VoidAsync(vehicle, cost.Id, ct);
            if (voided.TryGetFailure(out var voidFailure))
                return Result.Failure<BankTransactionView>(voidFailure!);
        }

        await links.ClearAsync(line.Id, TransactionLinkState.Unlinked, ct);
        // Its link's category gone, a rule takes over now, not at the next sync.
        await categoryRules.ApplyAsync(line.Id, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<IgnoreTransactionCommand, BankTransactionView>]
public sealed class IgnoreTransactionHandler(IContextAccessor context, BankLines lines, ILinkStore links)
    : IRequestHandler<IgnoreTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(IgnoreTransactionCommand command, CancellationToken ct = default)
    {
        var line = CheckedResources.Of<BankTransaction>(context, command);
        if (line!.LinkState != TransactionLinkState.Unlinked)
            return Result.Failure<BankTransactionView>(new ConflictFailure("This line is already linked or ignored.") { Code = BankingCodes.TransactionLinked });

        await links.ClearAsync(line.Id, TransactionLinkState.Ignored, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<SameLabelLinesQuery, IReadOnlyList<Guid>>]
public sealed class SameLabelLinesHandler(IContextAccessor context, BankingAccess access, IAccountStore accounts, ITransactionStore transactions)
    : IRequestHandler<SameLabelLinesQuery, IReadOnlyList<Guid>>
{
    /// <summary>The bulk categorisation's own limit, the line itself aside.</summary>
    public const int Most = SetTransactionsCategoryHandler.Most - 1;

    public async ValueTask<Result<IReadOnlyList<Guid>>> HandleAsync(SameLabelLinesQuery query, CancellationToken ct = default)
    {
        var line = CheckedResources.Of<BankTransaction>(context, query);
        var writable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).Where(r => r.CanWrite).Select(r => r.Account.Id).ToList();
        var same = await transactions.ListAsync(writable, null, null, 0, Most + 1, ct, uncategorised: true, normalizedLabel: line!.NormalizedLabel);
        return Result.Success<IReadOnlyList<Guid>>([.. same.Where(l => l.Id != line.Id).Take(Most).Select(l => l.Id)]);
    }
}

[RequestHandler<SetTransactionCategoryCommand, BankTransactionView>]
public sealed class SetTransactionCategoryHandler(IContextAccessor context, BankingAccess access, BankLines lines, ICategoryStore categories, SavingsFlows savingsFlows, IClock clock)
    : IRequestHandler<SetTransactionCategoryCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(SetTransactionCategoryCommand command, CancellationToken ct = default)
    {
        var found = BankLines.Categorisable(CheckedResources.Of<BankTransaction>(context, command));
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(found);
        var inUse = await InUseAsync(categories, command.CategoryId, ct);
        if (!inUse.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(inUse);

        await categories.SetForLinesAsync([line!.Id], command.CategoryId, CategorySource.Hand, $"person:{access.Person.Value:D}",
            clock.GetCurrentInstant(), byPerson: true, ct);
        await savingsFlows.RunForLineAsync(line.Id, access.Person, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }

    /// <summary>A category a line may take by hand: none, or one in use.</summary>
    internal static async Task<Result> InUseAsync(ICategoryStore categories, Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is not { } id)
            return Result.Success();
        if (await categories.FindAsync(id, ct) is not { } category)
            return Result.Failure(new NotFoundFailure("bank category", id.ToString()));
        // A merged category is archived too: its lines went to its target.
        return category.ArchivedAt is null ? Result.Success()
            : Result.Failure(FieldErrors.At(new Failure(BankingCodes.CategoryArchived, "A line takes only a category in use.", null), "categoryId"));
    }
}

[RequestHandler<SetTransactionsCategoryCommand, LinesCategorised>]
public sealed class SetTransactionsCategoryHandler(BankingAccess access, BankLines lines, ICategoryStore categories, SavingsFlows savingsFlows, IClock clock)
    : IRequestHandler<SetTransactionsCategoryCommand, LinesCategorised>
{
    /// <summary>Four pages of the operations list: a selection, never the whole history.</summary>
    public const int Most = 200;

    public async ValueTask<Result<LinesCategorised>> HandleAsync(SetTransactionsCategoryCommand command, CancellationToken ct = default)
    {
        var ids = command.TransactionIds.Distinct().ToList();
        if (ids.Count is 0 or > Most)
            return Result.Failure<LinesCategorised>(FieldErrors.At(
                new Failure(BankingCodes.CategoriseLines, $"Name 1 to {Most} lines.", null), "transactionIds"));
        var inUse = await SetTransactionCategoryHandler.InUseAsync(categories, command.CategoryId, ct);
        if (!inUse.IsSuccess)
            return BankingAccess.Failed<LinesCategorised>(inUse);

        var changed = new List<BankTransaction>();
        var refused = new List<LineRefusal>();
        foreach (var id in ids)
        {
            var found = await lines.CategorisableAsync(id, ct);
            if (found.TryGetValue(out var line))
                changed.Add(line!);
            else if (found.TryGetFailure(out var failure))
                refused.Add(new LineRefusal(id, failure!.Code));
        }
        if (changed.Count > 0)
        {
            await categories.SetForLinesAsync([.. changed.Select(l => l.Id)], command.CategoryId, CategorySource.Hand, $"person:{access.Person.Value:D}",
                clock.GetCurrentInstant(), byPerson: true, ct);
            await savingsFlows.RunAsync([.. changed.Select(l => l.Account).Distinct()], access.Person, ct);
        }
        return Result.Success(new LinesCategorised([.. changed.Select(l => l.Id)], refused));
    }
}

[RequestHandler<ResetTransactionCategoryCommand, BankTransactionView>]
public sealed class ResetTransactionCategoryHandler(
    IContextAccessor context, BankingAccess access, BankLines lines, ICategoryStore categories, LineRecategoriser recategoriser, SavingsFlows savingsFlows, IClock clock)
    : IRequestHandler<ResetTransactionCategoryCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(ResetTransactionCategoryCommand command, CancellationToken ct = default)
    {
        var line = CheckedResources.Of<BankTransaction>(context, command);
        if (line!.CategorySource == CategorySource.Split)
            return Result.Failure<BankTransactionView>(SplitUseCases.Split());

        await categories.SetForLinesAsync([line.Id], null, null, $"person:{access.Person.Value:D}", clock.GetCurrentInstant(), byPerson: true, ct);
        await recategoriser.ApplyAsync(line, ct);
        await savingsFlows.RunForLineAsync(line.Id, access.Person, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<ListBankRulesQuery, IReadOnlyList<BankRuleView>>]
public sealed class ListBankRulesHandler(BankingAccess access, IAccountStore accounts, IRuleStore rules, IItemRegistry items, IAccessPolicy policy)
    : IRequestHandler<ListBankRulesQuery, IReadOnlyList<BankRuleView>>
{
    public async ValueTask<Result<IReadOnlyList<BankRuleView>>> HandleAsync(ListBankRulesQuery query, CancellationToken ct = default)
    {

        var mine = await rules.ByAsync(access.Person, ct);
        var readable = (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).ToDictionary(r => r.Account.Id, r => r.Account.Name);
        var names = await items.FindManyAsync(
            [.. await policy.CheckManyAsync(access.Person, [.. mine.Select(r => r.TargetItem).Distinct()], AccessKind.Read, ct)], ct);
        return Result.Success<IReadOnlyList<BankRuleView>>([.. mine.Select(r => new BankRuleView(
            r.Id, r.LabelStem, readable.GetValueOrDefault(r.Account), r.TargetModule, r.TargetItem.Value, names.GetValueOrDefault(r.TargetItem)?.DisplayName,
            r.CreatedAt, r.LastUsedAt))]);
    }
}

[RequestHandler<DeleteBankRuleCommand, Guid>]
public sealed class DeleteBankRuleHandler(BankingAccess access, IRuleStore rules) : IRequestHandler<DeleteBankRuleCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteBankRuleCommand command, CancellationToken ct = default)
    {
        return await rules.DeleteAsync(command.Id, access.Person, ct)
            ? Result.Success(command.Id)
            : Result.Failure<Guid>(new NotFoundFailure("bank rule", command.Id.ToString()));
    }
}
