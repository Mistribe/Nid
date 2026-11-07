using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// 5C spec §6.4: `{ accountId, stem, lines[], subscription }`, where `subscription` is the body of <c>POST /api/v1/subscriptions/</c>.
/// The route's own proposal kind validates, previews and creates it; approved, the approver also links the group's lines still unlinked
/// to the new subscription and keeps a rule for the stem. Not editable: a wrong guess is rejected and the subscription made by hand.
/// </summary>
public sealed class SubscriptionProposal(
    IServiceProvider services, BankingAccess access, ITransactionStore transactions, ILinkStore links, IRuleStore rules, IClock clock)
    : IProposalHandler
{
    public const string KindName = "banking.create_subscription";
    private const string RouteKind = "subscriptions.create_subscription";

    public string Module => "banking";
    public string Kind => KindName;
    public int PayloadVersion => 1;
    public bool NeedsTarget => false;

    private sealed record Parsed(Guid Account, string Stem, IReadOnlyList<Guid> Lines, JsonElement Subscription);

    /// <summary>The account as a person tells it: its name and the IBAN's last four.</summary>
    internal static string Label(BankAccount account) => account.IbanMasked is { } last4 ? $"{account.Name} ·{last4}" : account.Name;

    // Resolved when called: ProposalHandlers takes every handler, this one included.
    private IProposalHandler Inner => services.GetServices<IProposalHandler>().Single(h => h.Kind == RouteKind);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var account = await access.AccountAsync(p!.Account, AccessKind.Read, ct);
        if (!account.IsSuccess)
            return Result.Failure<ProposalCheck>(FailureOf(account));
        var inner = await Inner.ValidateAsync(default, RouteCall(p), edited, ct);
        if (!inner.TryGetValue(out var check))
            return Result.Failure<ProposalCheck>(FailureOf(inner));
        return Result.Success(new ProposalCheck(null, check!.Warnings, TransactionLinker.Element(new JsonObject
        {
            ["accountId"] = p.Account.ToString("D"),
            ["stem"] = p.Stem,
            ["lines"] = new JsonArray([.. p.Lines.Select(l => (JsonNode)l.ToString("D"))]),
            ["subscription"] = JsonNode.Parse(check.Payload.GetProperty("body").GetRawText()),
        })));
    }

    public Failure? RefuseEdit(JsonElement submitted, JsonElement edited)
        => new(BankingCodes.ProposalNotEditable, "A proposed subscription is not edited: reject it and create it by hand.", null);

    /// <summary>The route's preview, with `{ account, cadence, lines [{ bookedOn, amount, currency }] }` for a reader of the account.</summary>
    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var p))
            return TransactionLinker.Element([]);
        var inner = await Inner.PreviewAsync(default, RouteCall(p!), ct);
        var preview = inner.ValueKind == JsonValueKind.Object ? JsonObject.Create(inner)! : [];
        if (!(await access.AccountAsync(p!.Account, AccessKind.Read, ct)).TryGetValue(out var account))
            return TransactionLinker.Element(preview);
        preview["account"] = Label(account!);
        preview["cadence"] = Cadence(p.Subscription);
        var lines = new JsonArray();
        foreach (var id in p.Lines)
            if (await transactions.FindAsync(id, ct) is { } line && line.Account == p.Account)
                lines.Add((JsonNode)new JsonObject
                {
                    ["bookedOn"] = TransactionLinker.Day(line.BookedOn), ["amount"] = TransactionLinker.Amount(line.Amount), ["currency"] = line.Currency.Code,
                });
        preview["lines"] = lines;
        return TransactionLinker.Element(preview);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var applied = await Inner.ApplyAsync(default, RouteCall(p!), ct);
        if (!applied.TryGetValue(out var ids))
            return applied;
        var subscription = new ItemId(ids![0]); // the route answers the created item's id
        var by = access.Person;
        var now = clock.GetCurrentInstant();
        foreach (var id in p!.Lines)
            if (await transactions.FindAsync(id, ct) is { LinkState: TransactionLinkState.Unlinked } line && line.Account == p.Account)
                await links.SetAsync(new BankLink(line.Id, "subscriptions", subscription, TransactionLinker.Day(line.BookedOn), null,
                    LinkOutcome.AsExpected, null, $"person:{by.Value:D}", now), TransactionLinkState.Linked, ct);
        await rules.UpsertAsync(p.Account, p.Stem, "subscriptions", subscription, by, ct);
        return applied;
    }

    /// <summary>The route proposal's payload: the body only, the route has no parameter.</summary>
    private static JsonElement RouteCall(Parsed p) => TransactionLinker.Element(new JsonObject { ["body"] = JsonNode.Parse(p.Subscription.GetRawText()) });

    private static string? Cadence(JsonElement subscription)
    {
        if (!subscription.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array || phases.GetArrayLength() == 0
            || !phases[0].TryGetProperty("every", out var every) || every.ValueKind != JsonValueKind.Object
            || !every.TryGetProperty("count", out var count) || !every.TryGetProperty("unit", out var unit))
            return null;
        return (count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0, unit.ValueKind == JsonValueKind.String ? unit.GetString() : null) switch
        {
            (1, "month") => "monthly",
            (3, "month") => "quarterly",
            (1, "year") => "yearly",
            _ => null,
        };
    }

    private static Result<Parsed> Parse(JsonElement payload)
    {
        var errors = new List<FieldError>();
        Guid account = default;
        if (!payload.TryGetProperty("accountId", out var a) || a.ValueKind != JsonValueKind.String || !a.TryGetGuid(out account))
            errors.Add(new FieldError("payload.accountId", FieldErrors.ShapeCode, "A UUID is required."));
        if (!payload.TryGetProperty("stem", out var s) || s.ValueKind != JsonValueKind.String || s.GetString() is not { Length: > 0 and <= 200 })
            errors.Add(new FieldError("payload.stem", FieldErrors.ShapeCode, "A label stem, 1 to 200 characters."));
        var lines = new List<Guid>();
        if (payload.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var line in l.EnumerateArray())
                if (line.ValueKind == JsonValueKind.String && line.TryGetGuid(out var id))
                    lines.Add(id);
        if (lines.Count == 0 || lines.Count != l.GetArrayLength())
            errors.Add(new FieldError("payload.lines", FieldErrors.ShapeCode, "The bank lines' UUIDs."));
        if (!payload.TryGetProperty("subscription", out var subscription) || subscription.ValueKind != JsonValueKind.Object)
            errors.Add(new FieldError("payload.subscription", FieldErrors.ShapeCode, "A JSON object is required."));
        return errors.Count > 0
            ? Result.Failure<Parsed>(FieldErrors.Shape(errors))
            : Result.Success(new Parsed(account, s.GetString()!, lines, subscription));
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();
}
