using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Assets.Contracts;
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
    IAccessPolicy policy, BankingAccess access, IProposalSubmitter proposals, IActorAccessor actor, IClock clock, LinkCategoryReader categories,
    IWritableVehicles vehicles)
{
    public const int AskDays = 45;

    /// <summary>#649: a vehicle's running cost, paid by an expense line; it has no expected charge, so it is offered as a candidate of its own.</summary>
    public const string Assets = "assets";

    /// <summary>The modules whose items a line may pay.</summary>
    public static bool Linkable(string? module) => module is "subscriptions" or "realestate" or "childcare" or Assets;

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
            return BankingAccess.Fail<BankTransaction>(BankingCodes.LinkTarget, "A line pays a subscription, a loan, a property, a childminder or a vehicle's running cost.");
        // Write first: an item the actor cannot read is not found, whatever its module.
        var write = await policy.CheckAsync(access.Person, item, AccessKind.Write, ct);
        if (!write.IsSuccess)
            return BankingAccess.Failed<BankTransaction>(write);
        if ((await items.FindAsync(item, ct))?.Module != module)
            return BankingAccess.Fail<BankTransaction>(BankingCodes.LinkTarget, "This item is not of that module.");
        if (module == Assets && (line.Amount >= 0 || !await vehicles.IsWritableVehicleAsync(access.Person, item, ct)))
            return BankingAccess.Fail<BankTransaction>(BankingCodes.LinkTarget, "An expense line pays a running cost of a vehicle.");
        return line.LinkState == TransactionLinkState.Unlinked
            ? Result.Success(line)
            : Result.Failure<BankTransaction>(new ConflictFailure("This line is already linked or ignored.") { Code = BankingCodes.TransactionLinked });
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
        BankTransaction line, string module, ItemId item, string? key, PersonId by, LocalDate today, CancellationToken ct, string? category = null)
    {
        var charge = await ChargeAsync(line, item, key, by, ct);
        // #649: the person picked the vehicle and the category: the cost is recorded now, so a line is never linked without its cost.
        string? recorded = null;
        if (module == Assets)
        {
            // #650: a line the bank files as fuel is, on a vehicle, its fuel or charging, whoever links it (web, MCP, proposal).
            var fallback = line.Category == CategorySeed.Find("transport.fuel")?.Id ? AssetCostCategories.Fuel : AssetCostCategories.Other;
            var cost = await RecordCostAsync(line, item, category ?? fallback, ct);
            if (!cost.TryGetValue(out var flow))
                return Result.Failure<BankLink>(cost.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
            recorded = $"flow:{flow:N}";
        }
        var link = await LinkToAsync(line, module, item, charge, $"person:{by.Value:D}", today, ct, recorded);
        var stem = TransactionLabel.Stem(line.NormalizedLabel);
        // A vehicle has no expected charge for a rule to match: its next line is linked by hand again.
        if (stem.Length == 0 || module == Assets)
            return Result.Success(link);

        var lines = await transactions.ListAsync([line.Account], null, null, 0, int.MaxValue, ct);
        var ruleStem = TransactionLabel.RuleStem(line.NormalizedLabel, lines.Where(l => l.BookedOn != line.BookedOn).Select(l => l.NormalizedLabel));
        var rule = await rules.UpsertAsync(line.Account, ruleStem, module, item, by, ct);
        foreach (var other in lines)
            if (other.Id != line.Id && other.LinkState == TransactionLinkState.Unlinked && TransactionLabel.RuleMatches(ruleStem, other.NormalizedLabel))
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
        BankTransaction line, string module, ItemId item, ExpectedCharge? charge, string decidedBy, LocalDate today, CancellationToken ct, string? recordedKey = null)
    {
        var ask = line.BookedOn >= today.PlusDays(-AskDays);
        var key = charge?.Key ?? recordedKey;
        var paid = Math.Abs(line.Amount);
        JsonElement? followUp = null;
        string? followUpKind = null;
        LinkOutcome outcome;
        if (charge is null)
        {
            outcome = line.Amount > 0 && (await items.FindAsync(item, ct))?.Kind == "property" ? LinkOutcome.Income : LinkOutcome.AsExpected;
        }
        else if (charge.Kind.Name == ChargeKinds.Rent)
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
            if (agrees == false && ask && charge.Kind.Name == ChargeKinds.Subscription && charge.Amount.Currency == line.Currency)
                (followUpKind, followUp) = ("subscriptions.change_price", Element(new JsonObject
                {
                    ["chargedOn"] = Day(charge.DueOn),
                    ["amount"] = new JsonObject { ["amount"] = Amount(paid), ["currency"] = line.Currency.Code },
                }));
        }

        var link = new BankLink(line.Id, module, item, key, null, outcome, charge?.Amount, decidedBy, clock.GetCurrentInstant());
        await links.SetAsync(link, TransactionLinkState.Linked, ct, await categories.KeyAsync(link, charge?.Kind.Name, line.Amount > 0, ct));
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

    /// <summary>
    /// #649: the cost the line pays on the vehicle, as the current actor: one already recorded (typed by hand, or from this line before) is reused,
    /// else it is recorded with <c>bank:{lineId}</c>, which the Core keeps unique per item. A refusal (a currency the vehicle does not hold) stops the link.
    /// </summary>
    private async Task<Result<Guid>> RecordCostAsync(BankTransaction line, ItemId vehicle, string category, CancellationToken ct)
    {
        if (await RecordedCostAsync(line, vehicle, ct) is { } known)
            return Result.Success(known);
        if (!Money.Create(Math.Abs(line.Amount), line.Currency).TryGetValue(out var amount))
            return BankingAccess.Fail<Guid>(BankingCodes.LinkTarget, "A line's amount has two decimals.");
        var recorded = await flows.RecordAsync(new NewFlow(vehicle, line.BookedOn, FlowKind.Expense, amount, category, $"bank:{line.Id:D}", BankSyncCollector.SourceLabel), ct);
        return recorded.TryGetValue(out var flow) ? Result.Success(flow!.Id) : Result.Failure<Guid>(recorded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
    }

    /// <summary>#649, as <see cref="RecordedRentAsync"/>: the vehicle's expense of the line's amount within 10 days that no other line is linked to.</summary>
    private async Task<Guid?> RecordedCostAsync(BankTransaction line, ItemId vehicle, CancellationToken ct)
    {
        var range = DateRange.Create(line.BookedOn.PlusDays(-10), line.BookedOn.PlusDays(11));
        if (!range.TryGetValue(out var days) || !(await flows.ListAsync(vehicle, days, new HashSet<FlowKind> { FlowKind.Expense }, ct)).TryGetValue(out var found))
            return null;
        foreach (var flow in found!)
            if (flow.Amount.Currency == line.Currency
                && (flow.ExternalRef?.StartsWith("bank:", StringComparison.Ordinal) != true || flow.ExternalRef == $"bank:{line.Id:D}")
                && Math.Round(flow.Amount.Amount, 2) == Math.Round(Math.Abs(line.Amount), 2) && !await links.FlowTakenAsync(flow.Id, ct))
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
