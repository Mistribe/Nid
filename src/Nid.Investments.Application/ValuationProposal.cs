using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>Spec 4C1 §6: `{ asOf, amount { amount, currency }, note? }` on an envelope; subject key: the date.</summary>
public sealed class ValuationProposal(
    IEnvelopeStore envelopes, IItemRegistry items, IHouseholdCalendar calendar, IValuations valuations, IInvoker invoker)
    : IProposalHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Module => "investments";
    public string Kind => "investments.record_valuation";
    public int PayloadVersion => 1;

    private sealed record Body(string? AsOf, AmountBody? Amount, string? Note);

    private sealed record AmountBody(string? Amount, string? Currency);

    private sealed record Parsed(LocalDate AsOf, Money Amount, string? Note);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(parsed.TryGetFailure(out var unreadable) ? unreadable! : throw new InvalidOperationException());
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, target, ct);
        if (!loaded.TryGetValue(out var envelope))
            return Result.Failure<ProposalCheck>(loaded.TryGetFailure(out var missing) ? missing! : throw new InvalidOperationException());
        if (EnvelopeCheck.RequireOpen(envelope.Item).TryGetFailure(out var closed))
            return Result.Failure<ProposalCheck>(closed!);
        if (!string.Equals(envelope.Item.Currency, p!.Amount.Currency.Code, StringComparison.Ordinal))
            return Field(CoreFactCodes.CurrencyMismatch, "The amount is not in the envelope's currency.", "payload.amount.currency");
        if (p.AsOf > await calendar.TodayAsync(ct))
            return Field(CoreFactCodes.DateInFuture, "A value is dated today or before.", "payload.asOf");
        return Result.Success(new ProposalCheck(Iso(p.AsOf), [], Normalized(p)));
    }

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
        => Parse(payload).TryGetValue(out var p)
            ? JsonSerializer.SerializeToElement(await CompareAsync(valuations, target, p!.AsOf, p.Amount, ct), Json)
            : JsonSerializer.SerializeToElement(new { current = (object?)null, previous = (object?)null, proposed = (object?)null });

    /// <summary>The value already recorded at that date, the one before it, and the proposed one (shared with the statement).</summary>
    internal static async Task<object> CompareAsync(IValuations valuations, ItemId target, LocalDate asOf, Money proposed, CancellationToken ct)
    {
        async Task<Valuation?> LatestAsync(LocalDate on) => (await valuations.LatestAsync(target, on, ct)).TryGetValue(out var latest) ? latest!.Value : null;
        var atDate = await LatestAsync(asOf);
        var current = atDate?.AsOf == asOf ? atDate : null;
        var previous = current is null ? atDate : await LatestAsync(asOf.PlusDays(-1));
        return new { current = View(current), previous = View(previous), proposed = new { asOf = Iso(asOf), amount = AmountView.From(proposed) } };
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(parsed.TryGetFailure(out var unreadable) ? unreadable! : throw new InvalidOperationException());
        var recorded = await invoker.InvokeAsync(new RecordValuationCommand(target, p!.AsOf, p.Amount, p.Note), ct);
        return recorded.Map(id => (IReadOnlyList<Guid>)[id]);
    }

    private static object? View(Valuation? v) => v is null ? null : new { asOf = Iso(v.AsOf), amount = AmountView.From(v.Value) };

    /// <summary>The known fields only: what is fingerprinted and stored.</summary>
    private static JsonElement Normalized(Parsed p)
    {
        var body = new JsonObject { ["asOf"] = Iso(p.AsOf), ["amount"] = JsonSerializer.SerializeToNode(AmountView.From(p.Amount), Json) };
        if (p.Note is not null) body["note"] = p.Note;
        return JsonSerializer.SerializeToElement(body);
    }

    private static Result<Parsed> Parse(JsonElement payload)
    {
        Body? body;
        try
        {
            body = payload.Deserialize<Body>(Json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) // InvalidOperationException: an undefined element
        {
            body = null;
        }
        var errors = new List<FieldError>();
        LocalDate asOf = default;
        if (body?.AsOf is not { } date || LocalDatePattern.Iso.Parse(date) is not { Success: true } parsedDate)
            errors.Add(new FieldError("payload.asOf", FieldErrors.ShapeCode, "An ISO date (yyyy-mm-dd) is required."));
        else
            asOf = parsedDate.Value;
        Money money = default;
        if (!Currency.TryCreate(body?.Amount?.Currency, out _))
            errors.Add(new FieldError("payload.amount.currency", FieldErrors.ShapeCode, "A currency (ISO code) is required."));
        else if (!Money.TryParse(body!.Amount!.Amount, body.Amount.Currency, out money))
            errors.Add(new FieldError("payload.amount.amount", FieldErrors.ShapeCode, "A decimal amount (e.g. '1234.56') is required."));
        if (body?.Note is { Length: > 500 })
            errors.Add(new FieldError("payload.note", FieldErrors.ShapeCode, "500 characters at most."));
        return errors.Count > 0 ? Result.Failure<Parsed>(FieldErrors.Shape(errors)) : Result.Success(new Parsed(asOf, money, body!.Note));
    }

    private static Result<ProposalCheck> Field(string code, string message, string path)
        => Result.Failure<ProposalCheck>(FieldErrors.At(new Failure(code, message, null), path));

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
