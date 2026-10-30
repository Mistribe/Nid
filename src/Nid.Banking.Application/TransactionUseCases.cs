using System.Text.Json;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
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
public sealed record ListBankTransactionsQuery(ItemId ItemId, LocalDate? From, LocalDate? To, int Page, string? State = null) : IQuery<BankTransactionPage>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>5C spec §8: the lines of every account the actor reads, cards included, newest first; <paramref name="State"/> defaults to <c>unlinked</c>.</summary>
public sealed record ListBankLinesQuery(string? State, LocalDate? From, LocalDate? To, int Page) : IQuery<BankTransactionPage>;

/// <summary>The charges within ±30 days of a line the actor reads that they may link it to, nearest amount first, 20 at most.</summary>
public sealed record LinkCandidatesQuery(Guid TransactionId) : IQuery<IReadOnlyList<ExpectedChargeView>>;

/// <summary>The charges the actor reads due in [<paramref name="From"/>, <paramref name="To"/>], 92 days at most.</summary>
public sealed record ListExpectedChargesQuery(LocalDate From, LocalDate To) : IQuery<IReadOnlyList<ExpectedChargeView>>;

/// <summary>A line of an account the actor writes linked by hand to a charge of an item they write, with its rule (<see cref="TransactionLinker.LinkAsync"/>).</summary>
public sealed record LinkTransactionCommand(Guid TransactionId, string Module, Guid ItemId, string? Key) : ICommand<BankTransactionView>;

/// <summary>A linked, internal or ignored line back to unlinked: write on its account, and on its link's target or read of its counterpart.</summary>
public sealed record UnlinkTransactionCommand(Guid TransactionId) : ICommand<BankTransactionView>;

/// <summary>An unlinked line no charge is expected for.</summary>
public sealed record IgnoreTransactionCommand(Guid TransactionId) : ICommand<BankTransactionView>;

/// <summary>The actor's own rules.</summary>
public sealed record ListBankRulesQuery : IQuery<IReadOnlyList<BankRuleView>>;

public sealed record DeleteBankRuleCommand(Guid Id) : ICommand<Guid>;

public sealed record BankTransactionPage(IReadOnlyList<BankTransactionView> Items, bool HasMore);

/// <summary>
/// <paramref name="Amount"/> is signed (a debit is negative); <paramref name="CardName"/> names the card of a <paramref name="FromCard"/> line,
/// <paramref name="AccountName"/> the line's own account. <paramref name="Link"/> is null when the line is not linked, or its target is
/// hidden from the reader (D19): <paramref name="LinkState"/> still says <c>linked</c> or <c>internal</c>.
/// </summary>
public sealed record BankTransactionView(
    Guid Id, LocalDate BookedOn, LocalDate? ValueOn, string Amount, string Currency, string Label, string? Counterparty, bool FromCard,
    string? CardName, string FetchedVia, string LinkState, LinkView? Link, string AccountName);

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
    BankingAccess access, IAccountStore accounts, ITransactionStore transactions, ILinkStore links, IItemRegistry items, IAccessPolicy policy)
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

    /// <summary>A page of the accounts' lines (null: every account the actor reads) in a <c>link_state</c>, or <c>all</c>.</summary>
    public async Task<BankTransactionPage> PageAsync(IReadOnlyList<Guid>? of, string state, LocalDate? from, LocalDate? to, int page, CancellationToken ct)
    {
        var readable = await ReadableAsync(ct);
        // One more than a page tells whether another follows.
        var lines = await transactions.ListAsync(of ?? [.. readable.Keys], from, to, (Math.Clamp(page, 1, MaxPage) - 1) * PageSize, PageSize + 1, ct,
            state == "all" ? null : state, [.. readable.Keys]);
        return new BankTransactionPage(await ViewsAsync([.. lines.Take(PageSize)], readable, ct), lines.Count > PageSize);
    }

    public async Task<BankTransactionView> ViewAsync(Guid id, CancellationToken ct)
        => (await ViewsAsync([(await transactions.FindAsync(id, ct))!], await ReadableAsync(ct), ct))[0];

    private async Task<Dictionary<Guid, BankAccount>> ReadableAsync(CancellationToken ct)
        => (await access.ReadableAsync(await accounts.AllAsync(ct), ct)).ToDictionary(r => r.Account.Id, r => r.Account);

    /// <summary>
    /// D19: a link shows only to a reader of its target item, or of its counterpart account; an internal line whose counterpart the reader
    /// cannot read says <c>linked</c>, as the store's state filter counts it.
    /// </summary>
    private async Task<IReadOnlyList<BankTransactionView>> ViewsAsync(
        IReadOnlyList<BankTransaction> lines, IReadOnlyDictionary<Guid, BankAccount> readable, CancellationToken ct)
    {
        var all = (await accounts.AllAsync(ct)).ToDictionary(a => a.Id);
        var found = await links.ForTransactionsAsync([.. lines.Select(l => l.Id)], ct);
        var targets = found.Values.Select(l => l.TargetItem).OfType<ItemId>().Distinct().ToList();
        var names = await items.FindManyAsync([.. await policy.CheckManyAsync(access.Person, targets, AccessKind.Read, ct)], ct);

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
                t.LinkState == TransactionLinkState.Internal && view is null ? "linked" : Wire(t.LinkState), view, account.Name);
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
public sealed class ListBankTransactionsHandler(BankingAccess access, IAccountStore accounts, BankLines lines)
    : IRequestHandler<ListBankTransactionsQuery, BankTransactionPage>
{
    public async ValueTask<Result<BankTransactionPage>> HandleAsync(ListBankTransactionsQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionPage>(enabled);
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
public sealed class ListBankLinesHandler(BankingAccess access, BankLines lines)
    : IRequestHandler<ListBankLinesQuery, BankTransactionPage>
{
    public async ValueTask<Result<BankTransactionPage>> HandleAsync(ListBankLinesQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionPage>(enabled);
        var parsed = BankLines.State(query.State, "unlinked");
        if (!parsed.TryGetValue(out var state))
            return BankingAccess.Failed<BankTransactionPage, string>(parsed);

        return Result.Success(await lines.PageAsync(null, state!, query.From, query.To, query.Page, ct));
    }
}

[RequestHandler<LinkCandidatesQuery, IReadOnlyList<ExpectedChargeView>>]
public sealed class LinkCandidatesHandler(BankingAccess access, BankLines lines, ExpectedChargeReader charges)
    : IRequestHandler<LinkCandidatesQuery, IReadOnlyList<ExpectedChargeView>>
{
    public const int Days = 30;
    public const int Most = 20;

    public async ValueTask<Result<IReadOnlyList<ExpectedChargeView>>> HandleAsync(LinkCandidatesQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<IReadOnlyList<ExpectedChargeView>>(enabled);
        var found = await lines.FindAsync(query.TransactionId, AccessKind.Read, ct);
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<IReadOnlyList<ExpectedChargeView>, BankTransaction>(found);

        var (from, to) = (line!.BookedOn.PlusDays(-Days), line.BookedOn.PlusDays(Days));
        var own = (await charges.WritableAsync(access.Person, from, to, ct)).Where(c => c.Credit == line.Amount > 0).ToList();
        var convert = await charges.ConverterAsync(own, from, to, ct);
        var stem = TransactionLabel.Stem(line.NormalizedLabel);
        var paid = Math.Abs(line.Amount);
        return Result.Success<IReadOnlyList<ExpectedChargeView>>([.. own
            .Select(c => (Charge: c, Gap: paid - convert(c, line.BookedOn, line.Currency.Code)))
            // Unknown gaps (no rate) last, then a hint found in the label, then the nearest date.
            .OrderBy(x => x.Gap is null).ThenBy(x => Math.Abs(x.Gap ?? 0))
            .ThenBy(x => !x.Charge.LabelHints.Any(h => TransactionLabel.Normalize(h) is { Length: > 0 } hint && stem.Contains(hint, StringComparison.Ordinal)))
            .ThenBy(x => Math.Abs(Period.DaysBetween(line.BookedOn, x.Charge.DueOn)))
            .Take(Most)
            .Select(x => ExpectedChargeViews.Of(x.Charge, x.Charge.Amount.Currency == line.Currency ? x.Gap : null))]);
    }
}

internal static class ExpectedChargeViews
{
    public static ExpectedChargeView Of(ExpectedCharge c, decimal? gap) => new(
        c.Module, c.Item.Value, c.ItemName, BankLines.Wire(c.Kind), c.Key, c.DueOn, TransactionLinker.Amount(c.Amount.Amount), c.Amount.Currency.Code,
        c.Credit, gap is { } g ? TransactionLinker.Amount(g) : null);
}

[RequestHandler<ListExpectedChargesQuery, IReadOnlyList<ExpectedChargeView>>]
public sealed class ListExpectedChargesHandler(BankingAccess access, ExpectedChargeReader charges)
    : IRequestHandler<ListExpectedChargesQuery, IReadOnlyList<ExpectedChargeView>>
{
    public const int MaxDays = 92;

    public async ValueTask<Result<IReadOnlyList<ExpectedChargeView>>> HandleAsync(ListExpectedChargesQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<IReadOnlyList<ExpectedChargeView>>(enabled);
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
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(enabled);
        var writable = await lines.FindAsync(command.TransactionId, AccessKind.Write, ct);
        if (!writable.IsSuccess)
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(writable);
        var item = new ItemId(command.ItemId);
        var checkedLine = await linker.CheckAsync(command.TransactionId, command.Module, item, ct);
        if (!checkedLine.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(checkedLine);

        var linked = await linker.LinkAsync(line!, command.Module, item, command.Key, access.Person, await calendar.TodayAsync(ct), ct);
        return linked.IsSuccess
            ? Result.Success(await lines.ViewAsync(line!.Id, ct))
            : BankingAccess.Failed<BankTransactionView, BankLink>(linked);
    }
}

[RequestHandler<UnlinkTransactionCommand, BankTransactionView>]
public sealed class UnlinkTransactionHandler(BankingAccess access, BankLines lines, ILinkStore links, IAccessPolicy policy, IItemRegistry items)
    : IRequestHandler<UnlinkTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(UnlinkTransactionCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(enabled);
        var found = await lines.FindAsync(command.TransactionId, AccessKind.Write, ct);
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(found);

        // D19: a link the actor cannot see is not found, never named.
        switch ((await links.ForTransactionsAsync([line!.Id], ct)).GetValueOrDefault(line.Id))
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

        await links.ClearAsync(line.Id, TransactionLinkState.Unlinked, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<IgnoreTransactionCommand, BankTransactionView>]
public sealed class IgnoreTransactionHandler(BankingAccess access, BankLines lines, ILinkStore links)
    : IRequestHandler<IgnoreTransactionCommand, BankTransactionView>
{
    public async ValueTask<Result<BankTransactionView>> HandleAsync(IgnoreTransactionCommand command, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<BankTransactionView>(enabled);
        var found = await lines.FindAsync(command.TransactionId, AccessKind.Write, ct);
        if (!found.TryGetValue(out var line))
            return BankingAccess.Failed<BankTransactionView, BankTransaction>(found);
        if (line!.LinkState != TransactionLinkState.Unlinked)
            return BankingAccess.Fail<BankTransactionView>(BankingCodes.TransactionLinked, "This line is already linked or ignored.");

        await links.ClearAsync(line.Id, TransactionLinkState.Ignored, ct);
        return Result.Success(await lines.ViewAsync(line.Id, ct));
    }
}

[RequestHandler<ListBankRulesQuery, IReadOnlyList<BankRuleView>>]
public sealed class ListBankRulesHandler(BankingAccess access, IAccountStore accounts, IRuleStore rules, IItemRegistry items, IAccessPolicy policy)
    : IRequestHandler<ListBankRulesQuery, IReadOnlyList<BankRuleView>>
{
    public async ValueTask<Result<IReadOnlyList<BankRuleView>>> HandleAsync(ListBankRulesQuery query, CancellationToken ct = default)
    {
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<IReadOnlyList<BankRuleView>>(enabled);

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
        var enabled = await access.EnabledAsync(ct);
        if (!enabled.IsSuccess)
            return BankingAccess.Failed<Guid>(enabled);
        return await rules.DeleteAsync(command.Id, access.Person, ct)
            ? Result.Success(command.Id)
            : Result.Failure<Guid>(new NotFoundFailure("bank rule", command.Id.ToString()));
    }
}
