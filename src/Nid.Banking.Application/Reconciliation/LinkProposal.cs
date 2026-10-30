using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// 5C spec §6.3 step 4: `{ transactionId, module, itemId, key? }` on the item a line pays; subject key: the line. Approved, the approver
/// links it as on the web (<see cref="TransactionLinker.LinkAsync"/>). Not editable: a wrong charge is rejected and linked by hand.
/// </summary>
public sealed class LinkProposal(TransactionLinker linker, IActorAccessor actor, IHouseholdCalendar calendar) : IProposalHandler
{
    public string Module => "banking";
    public string Kind => "banking.link_transaction";
    public int PayloadVersion => 1;

    private sealed record Parsed(Guid Transaction, string Module, ItemId Item, string? Key);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(target, payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var line = await linker.CheckAsync(p!.Transaction, p.Module, p.Item, ct);
        return line.IsSuccess
            ? Result.Success(new ProposalCheck(p.Transaction.ToString("D"), [], Normalized(p)))
            : Result.Failure<ProposalCheck>(FailureOf(line));
    }

    public Failure? RefuseEdit(JsonElement submitted, JsonElement edited)
        => new(BankingCodes.ProposalNotEditable, "A link is not edited: reject it and link the line by hand.", null);

    /// <summary>`{ transaction { bookedOn, label, amount, currency }, charge { itemName, dueOn, amount, currency }?, gap? }`.</summary>
    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var preview = new JsonObject();
        if (!Parse(target, payload).TryGetValue(out var p) || !(await linker.CheckAsync(p!.Transaction, p.Module, p.Item, ct)).TryGetValue(out var line))
            return TransactionLinker.Element(preview);
        preview["transaction"] = new JsonObject
        {
            ["bookedOn"] = TransactionLinker.Day(line!.BookedOn), ["label"] = line.Label,
            ["amount"] = TransactionLinker.Amount(line.Amount), ["currency"] = line.Currency.Code,
        };
        if (await linker.ChargeAsync(line, p.Item, p.Key, Person, ct) is { } charge)
        {
            preview["charge"] = new JsonObject
            {
                ["itemName"] = charge.ItemName, ["dueOn"] = TransactionLinker.Day(charge.DueOn),
                ["amount"] = TransactionLinker.Amount(charge.Amount.Amount), ["currency"] = charge.Amount.Currency.Code,
            };
            if (charge.Amount.Currency == line.Currency)
                preview["gap"] = TransactionLinker.Amount(Math.Abs(line.Amount) - charge.Amount.Amount);
        }
        return TransactionLinker.Element(preview);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(target, payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var checkedLine = await linker.CheckAsync(p!.Transaction, p.Module, p.Item, ct);
        if (!checkedLine.TryGetValue(out var line))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(checkedLine));
        var linked = await linker.LinkAsync(line!, p.Module, p.Item, p.Key, Person, await calendar.TodayAsync(ct), ct);
        return linked.IsSuccess ? Result.Success<IReadOnlyList<Guid>>([line!.Id]) : Result.Failure<IReadOnlyList<Guid>>(FailureOf(linked));
    }

    private PersonId Person => actor.Current!.PersonId!.Value;

    private static Result<Parsed> Parse(ItemId target, JsonElement payload)
    {
        var errors = new List<FieldError>();
        Guid transaction = default, item = default;
        if (!payload.TryGetProperty("transactionId", out var t) || t.ValueKind != JsonValueKind.String || !t.TryGetGuid(out transaction))
            errors.Add(new FieldError("payload.transactionId", FieldErrors.ShapeCode, "A UUID is required."));
        if (!payload.TryGetProperty("module", out var m) || m.ValueKind != JsonValueKind.String)
            errors.Add(new FieldError("payload.module", FieldErrors.ShapeCode, "A module is required."));
        if (!payload.TryGetProperty("itemId", out var i) || i.ValueKind != JsonValueKind.String || !i.TryGetGuid(out item) || item != target.Value)
            errors.Add(new FieldError("payload.itemId", FieldErrors.ShapeCode, "The proposal's target item."));
        string? key = null;
        if (payload.TryGetProperty("key", out var k) && k.ValueKind != JsonValueKind.Null)
        {
            if (k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 and <= 200 } given)
                key = given;
            else
                errors.Add(new FieldError("payload.key", FieldErrors.ShapeCode, "A charge's key, 1 to 200 characters."));
        }
        return errors.Count > 0
            ? Result.Failure<Parsed>(FieldErrors.Shape(errors))
            : Result.Success(new Parsed(transaction, m.GetString()!, new ItemId(item), key));
    }

    private static JsonElement Normalized(Parsed p)
    {
        var json = new JsonObject { ["transactionId"] = p.Transaction.ToString("D"), ["module"] = p.Module, ["itemId"] = p.Item.Value.ToString("D") };
        if (p.Key is not null)
            json["key"] = p.Key;
        return TransactionLinker.Element(json);
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();
}
