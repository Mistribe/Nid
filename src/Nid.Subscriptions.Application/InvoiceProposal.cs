using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.Application;

/// <summary>
/// Spec 4D1 §4: `{ chargedOn, amount }` on a subscription, all taxes included; subject key: the date. The price of the phase
/// open that day, to the cent, is « conforme »; another amount changes the price from that day (every, covers, cycles and
/// payer stay the replaced phase's).
/// </summary>
public sealed class InvoiceProposal(ISubscriptionStore store, IInvoker invoker) : IProposalHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Module => "subscriptions";
    public string Kind => "subscriptions.change_price";
    public int PayloadVersion => 1;

    private sealed record Body(string? ChargedOn, AmountBody? Amount);

    private sealed record AmountBody(string? Amount, string? Currency);

    private sealed record Parsed(LocalDate ChargedOn, Money Amount);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(parsed.TryGetFailure(out var unreadable) ? unreadable! : throw new InvalidOperationException());
        var input = await store.LoadAsync(target, ct);
        if (input is null)
            return Result.Failure<ProposalCheck>(new NotFoundFailure("subscription", target.ToString()));
        if (OpenOn(input, p!.ChargedOn) is not { } open)
            return Field("subscriptions.no_open_phase", "No phase is open on this date.", "payload.chargedOn");
        if (open.Price.Currency != p.Amount.Currency)
            return Field(CoreFactCodes.CurrencyMismatch, "The amount is not in the subscription's currency.", "payload.amount.currency");
        // Compare first: a matching invoice is « conforme » whatever the date (the phase's first day, or one just applied, sent again).
        if (open.Price.Amount == p.Amount.Amount)
            return Result.Success(new ProposalCheck(Iso(p.ChargedOn), [], Normalized(p), JsonSerializer.SerializeToElement(
                new { expected = new { price = MoneyView.From(open.Price) }, given = new { price = MoneyView.From(p.Amount) } }, Json)));
        // The rule of ChangePriceHandler, at the payload's path.
        if (p.ChargedOn <= open.StartsOn)
            return Field("subscriptions.phase_range", "A price change starts after the phase it replaces.", "payload.chargedOn");
        return Result.Success(new ProposalCheck(Iso(p.ChargedOn), [], Normalized(p)));
    }

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var p))
            return JsonSerializer.SerializeToElement(new { current = (object?)null, proposed = (object?)null, startsOn = (string?)null });
        var open = await store.LoadAsync(target, ct) is { } input ? OpenOn(input, p!.ChargedOn) : null;
        return JsonSerializer.SerializeToElement(new
        {
            current = open is null ? null : new { price = MoneyView.From(open.Price), every = DurationView.From(open.Every) },
            proposed = new { price = MoneyView.From(p!.Amount) },
            startsOn = Iso(p.ChargedOn),
        }, Json);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(parsed.TryGetFailure(out var unreadable) ? unreadable! : throw new InvalidOperationException());
        var changed = await invoker.InvokeAsync(new ChangePriceCommand(target, p!.ChargedOn, p.Amount, null, null, null, null), ct);
        return changed.TryGetValue(out var id)
            ? Result.Success((IReadOnlyList<Guid>)[id.Value])
            : Result.Failure<IReadOnlyList<Guid>>(AtPayload(changed.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException()));
    }

    /// <summary>The command's own field paths, as the payload's: `startsOn` is `chargedOn`, `price` the amount, anything else the payload.</summary>
    public static Failure AtPayload(Failure failure)
    {
        static string Path(string path) => path switch
        {
            "startsOn" => "payload.chargedOn",
            "price" => "payload.amount.amount",
            _ => "payload",
        };
        return FieldErrors.Of(failure) is { Count: > 0 } errors
            ? FieldErrors.Many(failure.Code, failure.Message, [.. errors.Select(e => e with { Path = Path(e.Path) })])
            : FieldErrors.At(failure, "payload");
    }

    /// <summary>The phase open on <paramref name="on"/>, by ChangePriceHandler's own rule (a phase that charged all its cycles is not open).</summary>
    private static BillingPhase? OpenOn(SubscriptionInput input, LocalDate on) => ChangePriceHandler.OpenPhase(input, on)?.Open;

    private static JsonElement Normalized(Parsed p)
        => JsonSerializer.SerializeToElement(new JsonObject
        {
            ["chargedOn"] = Iso(p.ChargedOn), ["amount"] = JsonSerializer.SerializeToNode(MoneyView.From(p.Amount), Json),
        });

    private static Result<Parsed> Parse(JsonElement payload)
    {
        Body? body;
        try
        {
            body = payload.Deserialize<Body>(Json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            body = null;
        }
        var errors = new List<FieldError>();
        LocalDate chargedOn = default;
        if (body?.ChargedOn is not { } date || LocalDatePattern.Iso.Parse(date) is not { Success: true } parsedDate)
            errors.Add(new FieldError("payload.chargedOn", FieldErrors.ShapeCode, "An ISO date (yyyy-mm-dd) is required."));
        else
            chargedOn = parsedDate.Value;
        Money amount = default;
        if (!Currency.TryCreate(body?.Amount?.Currency, out _))
            errors.Add(new FieldError("payload.amount.currency", FieldErrors.ShapeCode, "A currency (ISO code) is required."));
        else if (!Money.TryParse(body!.Amount!.Amount, body.Amount.Currency, out amount) || amount.Amount < 0)
            errors.Add(new FieldError("payload.amount.amount", FieldErrors.ShapeCode, "A decimal amount with a point (e.g. '12.99') is required."));
        return errors.Count > 0 ? Result.Failure<Parsed>(FieldErrors.Shape(errors)) : Result.Success(new Parsed(chargedOn, amount));
    }

    private static Result<ProposalCheck> Field(string code, string message, string path)
        => Result.Failure<ProposalCheck>(FieldErrors.At(new Failure(code, message, null), path));

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
