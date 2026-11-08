using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>
/// Holdings exposure spec C5: `{ isin, asOf, regions[{ code, pct }], sectors?[{ code, pct }] }` on an envelope; subject key: `isin:asOf`.
/// Checked by <see cref="BreakdownRules"/> (the API's rules, under `payload.`), applied through <see cref="RecordHoldingBreakdownCommand"/>
/// on the envelope's holding of that ISIN; applying returns the written row's id, which the document stamp marks.
/// </summary>
public sealed partial class BreakdownProposal(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, ISecurityBreakdownStore breakdowns, IHouseholdCalendar calendar, IInvoker invoker)
    : IProposalHandler
{
    public string Module => "investments";
    public string Kind => "investments.record_breakdown";
    public int PayloadVersion => 1;

    private sealed record Body(string? Isin, string? AsOf, List<PartBody?>? Regions, List<PartBody?>? Sectors);

    private sealed record PartBody(string? Code, decimal? Pct);

    private sealed record Parsed(string Isin, LocalDate AsOf, IReadOnlyList<BreakdownPart> Regions, IReadOnlyList<BreakdownPart>? Sectors);

    private sealed record Shown(string AsOf, IReadOnlyList<BreakdownPart> Regions, IReadOnlyList<BreakdownPart>? Sectors);

    private sealed record Preview(string HoldingName, Shown Proposed, Shown? Current);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Body))]
    [JsonSerializable(typeof(Preview))]
    [JsonSerializable(typeof(JsonObject))]
    private sealed partial class BreakdownJson : JsonSerializerContext;

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, target, ct);
        if (!loaded.TryGetValue(out var envelope))
            return Result.Failure<ProposalCheck>(FailureOf(loaded));
        if (EnvelopeCheck.RequireOpen(envelope.Item).TryGetFailure(out var closed))
            return Result.Failure<ProposalCheck>(closed!);
        var holding = await HoldingOfAsync(target, p!.Isin, ct);
        if (!holding.TryGetValue(out _))
            return Result.Failure<ProposalCheck>(FieldErrors.At(FailureOf(holding), "payload.isin"));
        if (p.AsOf > await calendar.TodayAsync(ct))
            return Result.Failure<ProposalCheck>(FieldErrors.At(new Failure(FieldErrors.ShapeCode, "A breakdown is not dated in the future.", null), "payload.asOf"));
        var errors = BreakdownRules.Check(p.Regions, p.Sectors).Select(e => new FieldError($"payload.{e.Path}", e.Code, e.Message)).ToList();
        if (errors.Count > 0)
            return Result.Failure<ProposalCheck>(errors.Any(e => e.Code == BreakdownCodes.Not100)
                ? FieldErrors.Many(BreakdownCodes.Not100, "Some fields are not valid.", errors)
                : FieldErrors.Shape(errors));
        return Result.Success(new ProposalCheck($"{p.Isin}:{Iso(p.AsOf)}", [], Normalized(p)));
    }

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var p))
            return JsonSerializer.SerializeToElement(new JsonObject(), BreakdownJson.Default.JsonObject);
        var name = (await HoldingOfAsync(target, p!.Isin, ct)).TryGetValue(out var h) ? h!.Name : p.Isin;
        var current = (await breakdowns.LatestAsync([p.Isin], ct)).TryGetValue(p.Isin, out var b) ? new Shown(Iso(b.AsOf), b.Regions, b.Sectors) : null;
        return JsonSerializer.SerializeToElement(new Preview(name, new Shown(Iso(p.AsOf), p.Regions, p.Sectors), current), BreakdownJson.Default.Preview);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var holding = await HoldingOfAsync(target, p!.Isin, ct);
        if (!holding.TryGetValue(out var h))
            return Result.Failure<IReadOnlyList<Guid>>(FieldErrors.At(FailureOf(holding), "payload.isin"));
        var written = await invoker.InvokeAsync(new RecordHoldingBreakdownCommand(target, h!.Id, p.AsOf, p.Regions, p.Sectors), ct);
        return written.TryGetValue(out var id)
            ? Result.Success<IReadOnlyList<Guid>>([id])
            : Result.Failure<IReadOnlyList<Guid>>(FailureOf(written));
    }

    /// <summary>The envelope's holding of this ISIN that has a market exposure; one that only a euro fund or cash carries is refused.</summary>
    private async Task<Result<EnvelopeHolding>> HoldingOfAsync(ItemId target, string isin, CancellationToken ct)
    {
        var carrying = (await holdings.HoldingsAsync(target, ct)).Select(h => h.Holding).Where(h => string.Equals(h.Isin, isin, StringComparison.OrdinalIgnoreCase)).ToList();
        if (carrying.Count == 0)
            return Result.Failure<EnvelopeHolding>(new Failure(FieldErrors.ShapeCode, "No holding of this envelope has this ISIN.", null));
        return carrying.FirstOrDefault(h => h.Kind is not ("euro_fund" or "cash")) is { } market
            ? Result.Success(market)
            : Result.Failure<EnvelopeHolding>(new Failure(BreakdownCodes.NotMarket, "A euro fund or cash has no market exposure to break down.", null));
    }

    private static JsonElement Normalized(Parsed p)
    {
        JsonArray Parts(IReadOnlyList<BreakdownPart> parts)
            => new(parts.Select(x => (JsonNode?)new JsonObject { ["code"] = x.Code, ["pct"] = x.Pct }).ToArray());
        var body = new JsonObject { ["isin"] = p.Isin, ["asOf"] = Iso(p.AsOf), ["regions"] = Parts(p.Regions) };
        if (p.Sectors is not null)
            body["sectors"] = Parts(p.Sectors);
        return JsonSerializer.SerializeToElement(body, BreakdownJson.Default.JsonObject);
    }

    private static Result<Parsed> Parse(JsonElement payload)
    {
        Body? body;
        try
        {
            body = payload.Deserialize(BreakdownJson.Default.Body);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            body = null;
        }
        var errors = new List<FieldError>();
        void Error(string path, string message) => errors.Add(new FieldError(path, FieldErrors.ShapeCode, message));

        var isin = body?.Isin?.Trim().ToUpperInvariant();
        if (isin is not { Length: 12 })
            Error("payload.isin", "An ISIN (12 characters) is required.");
        LocalDate asOf = default;
        if (body?.AsOf is not { } date || LocalDatePattern.Iso.Parse(date) is not { Success: true } parsedDate)
            Error("payload.asOf", "An ISO date (yyyy-mm-dd) is required.");
        else
            asOf = parsedDate.Value;
        var regions = Parts("regions", body?.Regions, required: true);
        var sectors = Parts("sectors", body?.Sectors, required: false);

        List<BreakdownPart>? Parts(string name, List<PartBody?>? raw, bool required)
        {
            if (raw is null)
            {
                if (required) Error($"payload.{name}", "The shares are required.");
                return null;
            }
            var parts = new List<BreakdownPart>();
            for (var i = 0; i < raw.Count; i++)
            {
                if (raw[i] is not { Code: { } code, Pct: { } pct })
                    Error($"payload.{name}[{i}]", "A code and a share (pct) are required.");
                else
                    parts.Add(new BreakdownPart(code, pct));
            }
            return parts;
        }

        return errors.Count > 0
            ? Result.Failure<Parsed>(FieldErrors.Shape(errors))
            : Result.Success(new Parsed(isin!, asOf, regions!, sectors));
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
