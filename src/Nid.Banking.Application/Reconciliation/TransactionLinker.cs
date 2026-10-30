using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// 5C spec §6.2, D20: a line linked to a charge of an item, by a person (the web, an approved proposal) or by their rule, with the
/// outcome's follow-up: a price change of a subscription, a rent's cash-in, nothing else. Anything that asks a human is only made for a
/// line booked within the last <see cref="AskDays"/> days.
/// </summary>
public sealed class TransactionLinker(
    ExpectedChargeReader charges, ITransactionStore transactions, ILinkStore links, IRuleStore rules, IFlows flows, IItemRegistry items,
    IAccessPolicy policy, BankingAccess access, IProposalSubmitter proposals, IActorAccessor actor, IClock clock)
{
    public const int AskDays = 45;

    /// <summary>The modules whose items a line may pay.</summary>
    public static bool Linkable(string? module) => module is "subscriptions" or "realestate";

    /// <summary>
    /// The line, when the actor may link it to <paramref name="item"/>: they write its account, as unlinking asks (not found when they cannot
    /// read it, forbidden when they only read it), write the item, the item is of a linkable <paramref name="module"/>
    /// (<see cref="BankingCodes.LinkTarget"/>), and the line is unlinked (<see cref="BankingCodes.TransactionLinked"/>).
    /// </summary>
    public async Task<Result<BankTransaction>> CheckAsync(Guid transaction, string module, ItemId item, CancellationToken ct)
    {
        if (await transactions.FindAsync(transaction, ct) is not { } line)
            return Result.Failure<BankTransaction>(new NotFoundFailure("bank transaction", transaction.ToString()));
        if ((await access.AccountAsync(line.Account, AccessKind.Write, ct)).TryGetFailure(out var denied))
            return Result.Failure<BankTransaction>(denied is UnauthorizedFailure ? denied : new NotFoundFailure("bank transaction", transaction.ToString()));
        if (!Linkable(module))
            return BankingAccess.Fail<BankTransaction>(BankingCodes.LinkTarget, "A line pays a subscription, a loan or a property.");
        // Write first: an item the actor cannot read is not found, whatever its module.
        var write = await policy.CheckAsync(access.Person, item, AccessKind.Write, ct);
        if (!write.IsSuccess)
            return BankingAccess.Failed<BankTransaction>(write);
        if ((await items.FindAsync(item, ct))?.Module != module)
            return BankingAccess.Fail<BankTransaction>(BankingCodes.LinkTarget, "This item is not of that module.");
        return line.LinkState == TransactionLinkState.Unlinked
            ? Result.Success(line)
            : BankingAccess.Fail<BankTransaction>(BankingCodes.TransactionLinked, "This line is already linked or ignored.");
    }

    /// <summary>
    /// The charge a line of <paramref name="item"/> pays, as <paramref name="person"/> may write it: the one of <paramref name="key"/>, else
    /// the nearest within tolerance; null when none.
    /// </summary>
    public async Task<ExpectedCharge?> ChargeAsync(BankTransaction line, ItemId item, string? key, PersonId person, CancellationToken ct)
    {
        var own = (await charges.WritableAsync(person, line.BookedOn.PlusDays(-AskDays), line.BookedOn.PlusDays(AskDays), ct))
            .Where(c => c.Item == item).ToList();
        return key is not null
            ? own.FirstOrDefault(c => c.Key == key)
            : ChargeMatch.Near(LineOf(line), [.. own.Select(DueOf)]) is [var nearest, ..] ? own[nearest] : null;
    }

    /// <summary>
    /// A person's link (the web, an approved proposal): the link, their rule for the line's stem (unless it is empty), the follow-up, then
    /// that rule on the account's other unlinked lines of the stem. Checked by <see cref="CheckAsync"/> first.
    /// </summary>
    public async Task<Result<BankLink>> LinkAsync(
        BankTransaction line, string module, ItemId item, string? key, PersonId by, LocalDate today, CancellationToken ct)
    {
        var charge = await ChargeAsync(line, item, key, by, ct);
        var link = await LinkToAsync(line, module, item, charge, $"person:{by.Value:D}", today, ct);
        var stem = TransactionLabel.Stem(line.NormalizedLabel);
        if (stem.Length == 0)
            return Result.Success(link);

        var rule = await rules.UpsertAsync(line.Account, stem, module, item, by, ct);
        foreach (var other in await transactions.ListAsync([line.Account], null, null, 0, int.MaxValue, ct, "unlinked"))
            if (other.Id != line.Id && TransactionLabel.Stem(other.NormalizedLabel) == stem)
                await ApplyRuleAsync(other, rule, today, ct);
        return Result.Success(link);
    }

    /// <summary>A rule's link (<c>rule:{id}</c>), made and followed up as its creator; false when no charge of the item is within tolerance.</summary>
    public async Task<bool> ApplyRuleAsync(BankTransaction line, BankRule rule, LocalDate today, CancellationToken ct)
    {
        var saved = actor.Current;
        actor.Current = Actor.BankSync(rule.CreatedBy);
        try
        {
            if (await ChargeAsync(line, rule.TargetItem, null, rule.CreatedBy, ct) is not { } charge)
                return false;
            await LinkToAsync(line, rule.TargetModule, rule.TargetItem, charge, $"rule:{rule.Id:D}", today, ct);
            await rules.TouchAsync(rule.Id, clock.GetCurrentInstant(), ct);
            return true;
        }
        finally
        {
            actor.Current = saved;
        }
    }

    /// <summary>Global Constraints « Link outcomes and their follow-ups », as the current actor.</summary>
    private async Task<BankLink> LinkToAsync(
        BankTransaction line, string module, ItemId item, ExpectedCharge? charge, string decidedBy, LocalDate today, CancellationToken ct)
    {
        var ask = line.BookedOn >= today.PlusDays(-AskDays);
        var key = charge?.Key;
        var paid = Math.Abs(line.Amount);
        JsonElement? followUp = null;
        string? followUpKind = null;
        LinkOutcome outcome;
        if (charge is null)
            outcome = line.Amount > 0 && (await items.FindAsync(item, ct))?.Kind == "property" ? LinkOutcome.Income : LinkOutcome.AsExpected;
        else if (charge.Kind == ChargeKind.Rent)
        {
            // D20: a rent already recorded (by hand, by the routine) is only linked; else its cash-in is proposed.
            outcome = LinkOutcome.Income;
            if (await RecordedRentAsync(line, item, ct) is { } flow)
                key = $"flow:{flow:N}";
            else if (ask)
                (followUpKind, followUp) = ("realestate.add_property_flow", Element(new JsonObject
                {
                    ["id"] = item.Value.ToString(),
                    ["body"] = new JsonObject
                    {
                        ["occurredOn"] = Day(line.BookedOn), ["kind"] = "income", ["amount"] = Amount(paid), ["category"] = "rent",
                        ["note"] = BankSyncCollector.SourceLabel, ["externalRef"] = $"bank:{line.Id:D}",
                    },
                }));
        }
        else
        {
            var converted = (await charges.ConverterAsync([charge], line.BookedOn, line.BookedOn, ct))(charge, line.BookedOn, line.Currency.Code);
            // No rate: the person's link stands, without a follow-up a guess would make.
            var agrees = ChargeMatch.Agrees(LineOf(line), DueOf(charge, 0), converted);
            outcome = agrees == true ? LinkOutcome.AsExpected : LinkOutcome.PriceDiffers;
            if (agrees == false && ask && charge.Kind == ChargeKind.Subscription && charge.Amount.Currency == line.Currency)
                (followUpKind, followUp) = ("subscriptions.change_price", Element(new JsonObject
                {
                    ["chargedOn"] = Day(charge.DueOn),
                    ["amount"] = new JsonObject { ["amount"] = Amount(paid), ["currency"] = line.Currency.Code },
                }));
        }

        var link = new BankLink(line.Id, module, item, key, null, outcome, charge?.Amount, decidedBy, clock.GetCurrentInstant());
        await links.SetAsync(link, TransactionLinkState.Linked, ct);
        // A refused follow-up (a price change the subscription's phases refuse) leaves the link as it is. §0 n°1: a follow-up carries the
        // line's amount to every reader of the item, so only when they all read its account; else the account-filtered findings speak.
        if (followUp is { } payload && await access.ItemReadersReadAsync(line.Account, item, ct))
            await proposals.SubmitAsync(followUpKind!, item, payload, BankSyncCollector.SourceLabel, ct);
        return link;
    }

    /// <summary>D20: the property's rent or charges income of the line's amount within 10 days that no other line is linked to.</summary>
    private async Task<Guid?> RecordedRentAsync(BankTransaction line, ItemId property, CancellationToken ct)
    {
        var range = DateRange.Create(line.BookedOn.PlusDays(-10), line.BookedOn.PlusDays(11));
        if (!range.TryGetValue(out var days) || !(await flows.ListAsync(property, days, new HashSet<FlowKind> { FlowKind.Income }, ct)).TryGetValue(out var found))
            return null;
        foreach (var flow in found!)
            // Another line's cash-in keeps its `rent:` link key, so FlowTakenAsync misses it: its `bank:` reference tells it.
            if (flow.Category is "rent" or "recoverable_charges" && flow.Amount.Currency == line.Currency
                && (flow.ExternalRef?.StartsWith("bank:", StringComparison.Ordinal) != true || flow.ExternalRef == $"bank:{line.Id:D}")
                && Math.Round(flow.Amount.Amount, 2) == Math.Round(line.Amount, 2) && !await links.FlowTakenAsync(flow.Id, ct))
                return flow.Id;
        return null;
    }

    internal static Line LineOf(BankTransaction line)
        => new(line.Id, line.Account, line.BookedOn, line.Amount, line.Currency.Code, TransactionLabel.Stem(line.NormalizedLabel));

    internal static Due DueOf(ExpectedCharge charge, int index)
        => new(index, charge.DueOn, charge.Amount.Amount, charge.Credit, charge.Amount.Currency.Code, charge.ToleranceDays);

    internal static string Amount(decimal amount) => amount.ToString("0.00##", CultureInfo.InvariantCulture);

    internal static string Day(LocalDate on) => on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static JsonElement Element(JsonObject json)
    {
        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.Clone();
    }
}
