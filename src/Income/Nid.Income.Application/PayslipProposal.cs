using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Income.Application;

/// <summary>Spec 4D1 D6: a net within this ratio of the expected net is « conforme » (setting <c>Income:PayslipTolerance</c>, default 0.02).</summary>
public sealed record PayslipTolerance(decimal Ratio)
{
    /// <summary>The setting's text (null: the default); a value outside 0 &lt;= x &lt; 1, or not an invariant decimal, is a configuration error.</summary>
    public static PayslipTolerance Parse(string? setting)
        => setting is null
            ? new(0.02m)
            : decimal.TryParse(setting, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ratio) && ratio is >= 0 and < 1
                ? new(ratio)
                : throw new InvalidOperationException($"Income:PayslipTolerance must be a decimal with a point, at least 0 and below 1 (got '{setting}').");
}

/// <summary>
/// Spec 4D1 §3: `{ paidOn, phaseLabel, net, gross?, taxable?, effectiveOn }` on an income source; subject key: the label and the month
/// of `paidOn`. A payslip corrects the expected pay (D3): the phase running on `paidOn` changes from its payment date in that
/// month; no « received » amount is recorded.
/// </summary>
public sealed partial class PayslipProposal(
    ISourceStore sources, IItemRegistry items, IPhaseStore phases, PayslipTolerance tolerance, IInvoker invoker) : IProposalHandler
{
    public string Module => "income";
    public string Kind => "income.change_pay";
    public int PayloadVersion => 1;

    private sealed record Body(string? PaidOn, string? PhaseLabel, AmountBody? Net, AmountBody? Gross, AmountBody? Taxable, string? EffectiveOn);

    private sealed record AmountBody(string? Amount, string? Currency);

    private sealed record Parsed(LocalDate PaidOn, string? PhaseLabel, Money Net, Money? Gross, Money? Taxable, LocalDate? EffectiveOn);

    private sealed record Pay(IncomeMoneyView Net, IncomeMoneyView? Gross, IncomeMoneyView? Taxable);

    private sealed record Comparison(Pay Expected, Pay Given);

    private sealed record Preview(Pay? Expected, Pay? Proposed, string? EffectiveOn);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Body))]
    [JsonSerializable(typeof(Comparison))]
    [JsonSerializable(typeof(Preview))]
    [JsonSerializable(typeof(JsonObject))]
    private sealed partial class PayslipJson : JsonSerializerContext;

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var found = await FindPhaseAsync(target, p!, ct);
        if (!found.TryGetValue(out var phase))
            return Result.Failure<ProposalCheck>(FailureOf(found));

        var expected = phase.NetAmount.Amount;
        var sameNet = Math.Abs(p!.Net.Amount - expected) <= tolerance.Ratio * expected;
        var sameGross = p.Gross is not { } gross || phase.GrossAmount is not { } expectedGross || gross.Amount == expectedGross.Amount;
        // A net taxable income the payslip prints and the phase lacks or has otherwise is a pay change too (no tolerance, like the gross).
        var sameTaxable = p.Taxable is not { } taxable || phase.TaxableAmount is { } expectedTaxable && taxable.Amount == expectedTaxable.Amount;
        var key = $"{phase!.Label}:{p.PaidOn.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
        // Compare first: a matching payslip needs no effective date (so one in the phase's first month, or sent twice, is « conforme »).
        if (sameNet && sameGross && sameTaxable)
            return Result.Success(new ProposalCheck(key, [], Normalized(p with { PhaseLabel = phase.Label }),
                JsonSerializer.SerializeToElement(new Comparison(PayOf(phase.NetAmount, phase.GrossAmount, phase.TaxableAmount), PayOf(p.Net, p.Gross, p.Taxable)), PayslipJson.Default.Comparison)));
        var on = EffectiveOn(phase, p);
        if (!on.TryGetValue(out var effectiveOn))
            return Result.Failure<ProposalCheck>(FailureOf(on));
        return Result.Success(new ProposalCheck(key, [], Normalized(p with { PhaseLabel = phase.Label, EffectiveOn = effectiveOn }), null));
    }

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        if (!Parse(payload).TryGetValue(out var p) || !(await FindPhaseAsync(target, p!, ct)).TryGetValue(out var phase))
            return JsonSerializer.SerializeToElement(new Preview(null, null, null), PayslipJson.Default.Preview);
        return JsonSerializer.SerializeToElement(new Preview(
            PayOf(phase!.NetAmount, phase.GrossAmount, phase.TaxableAmount), PayOf(p!.Net, p.Gross, p.Taxable),
            EffectiveOn(phase, p).TryGetValue(out var on) ? Iso(on) : null), PayslipJson.Default.Preview);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        var found = await FindPhaseAsync(target, p!, ct);
        if (!found.TryGetValue(out var phase))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(found));
        var on = EffectiveOn(phase!, p!);
        if (!on.TryGetValue(out var effectiveOn))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(on));
        // The cadence, the note and (when the payslip prints none) the gross stay the replaced phase's; the taxable is the payslip's alone.
        var changed = await invoker.InvokeAsync(
            new ChangeIncomePhaseCommand(target, phase!.Label, effectiveOn, p!.Net, null, p.Gross ?? phase.GrossAmount, phase.Note, p.Taxable), ct);
        return changed.TryGetValue(out var id)
            ? Result.Success<IReadOnlyList<Guid>>([id])
            : Result.Failure<IReadOnlyList<Guid>>(AtPayload(FailureOf(changed)));
    }

    /// <summary>
    /// The phase running on `paidOn` (the named one, or the only one), as it runs on the month's expected payment date: a
    /// payslip dated before that date, once its change is applied there, is compared with the phase that continues it.
    /// </summary>
    private async Task<Result<IncomePhase>> FindPhaseAsync(ItemId target, Parsed p, CancellationToken ct)
    {
        var loaded = await OpenSource.LoadAsync(sources, items, phases, target, ct);
        if (!loaded.TryGetValue(out var source))
            return Result.Failure<IncomePhase>(FailureOf(loaded));
        if (p.Net.Currency != source.Currency)
            return Field<IncomePhase>(CoreFactCodes.CurrencyMismatch, "The net is not in the source's currency.", "payload.net.currency");
        if (p.Gross is { } gross && gross.Currency != source.Currency)
            return Field<IncomePhase>(CoreFactCodes.CurrencyMismatch, "The gross is not in the source's currency.", "payload.gross.currency");
        if (p.Taxable is { } taxable && taxable.Currency != source.Currency)
            return Field<IncomePhase>(CoreFactCodes.CurrencyMismatch, "The taxable amount is not in the source's currency.", "payload.taxable.currency");

        var running = source.Phases.Where(x => IncomeMath.IsActive(x, p.PaidOn) && (p.PhaseLabel is null || x.Label == p.PhaseLabel)).ToList();
        if (running.Count == 0)
            return p.PhaseLabel is null
                ? Field<IncomePhase>(IncomeCodes.NoRunningPhase, "No phase runs on that date.", "payload.paidOn")
                : Field<IncomePhase>(IncomeCodes.NoRunningPhase, $"No '{p.PhaseLabel}' phase runs on that date.", "payload.phaseLabel");
        if (running.Count > 1)
            return Field<IncomePhase>(FieldErrors.ShapeCode, "Several phases run on that date: name one.", "payload.phaseLabel");
        var phase = running[0];
        var month = new LocalDate(p.PaidOn.Year, p.PaidOn.Month, 1);
        // Its cadence regardless of its end: a phase changed on its payment date ends on that very date.
        var expectedOn = IncomeMath.Occurrences(phase with { EndsOn = null }, month, month.PlusMonths(1)).Select(o => (LocalDate?)o.On).FirstOrDefault();
        return Result.Success(expectedOn is { } on
            ? source.Phases.FirstOrDefault(x => x.Label == phase.Label && IncomeMath.IsActive(x, on)) ?? phase
            : phase);
    }

    /// <summary>The date the change takes effect: the edit's, or the month's payment date; the rule of ChangeIncomePhaseCommand.</summary>
    private static Result<LocalDate> EffectiveOn(IncomePhase phase, Parsed p)
    {
        if (p.EffectiveOn is { } chosen)
            return chosen > phase.StartsOn && IncomeMath.Occurrences(phase, chosen, chosen.PlusDays(1)).Any()
                ? Result.Success(chosen)
                : Field<LocalDate>(IncomeCodes.InvalidPhase, "A change takes effect on a payment date of the phase, after its start.", "payload.effectiveOn");
        var month = new LocalDate(p.PaidOn.Year, p.PaidOn.Month, 1);
        var expectedOn = IncomeMath.Occurrences(phase, month, month.PlusMonths(1)).Select(o => (LocalDate?)o.On).FirstOrDefault();
        if (expectedOn is not { } on)
            return Field<LocalDate>(IncomeCodes.InvalidPhase, "This phase expects no payment that month.", "payload.paidOn");
        return on > phase.StartsOn
            ? Result.Success(on)
            : Field<LocalDate>(IncomeCodes.InvalidPhase,
                "That month's payment is the first of the current phase (a change already took effect then): "
                + "a different pay can only start from a later payment.", "payload.paidOn");
    }

    /// <summary>The change-phase command's own field as the payload's: `effectiveOn` and `startsOn` are `effectiveOn`, `label` the phase's label, anything else the payload.</summary>
    public static Failure AtPayload(Failure failure)
        => FieldErrors.At(failure, failure.Metadata is { } m && m.TryGetValue("field", out var field) ? field switch
        {
            "effectiveOn" or "startsOn" => "payload.effectiveOn",
            "label" => "payload.phaseLabel",
            _ => "payload",
        } : "payload");

    private static Pay PayOf(Money net, Money? gross, Money? taxable)
        => new(IncomeMoneyView.From(net), gross is { } g ? IncomeMoneyView.From(g) : null, taxable is { } t ? IncomeMoneyView.From(t) : null);

    /// <summary>The known fields only, the label and the effective date resolved: what is fingerprinted and stored.</summary>
    private static JsonElement Normalized(Parsed p)
    {
        var body = new JsonObject
        {
            ["paidOn"] = Iso(p.PaidOn), ["phaseLabel"] = p.PhaseLabel, ["net"] = JsonSerializer.SerializeToNode(IncomeMoneyView.From(p.Net), PayslipJson.Default.IncomeMoneyView),
        };
        if (p.Gross is { } gross)
            body["gross"] = JsonSerializer.SerializeToNode(IncomeMoneyView.From(gross), PayslipJson.Default.IncomeMoneyView);
        if (p.Taxable is { } taxable)
            body["taxable"] = JsonSerializer.SerializeToNode(IncomeMoneyView.From(taxable), PayslipJson.Default.IncomeMoneyView);
        if (p.EffectiveOn is { } effectiveOn)
            body["effectiveOn"] = Iso(effectiveOn);
        return JsonSerializer.SerializeToElement(body, PayslipJson.Default.JsonObject);
    }

    private static Result<Parsed> Parse(JsonElement payload)
    {
        Body? body;
        try
        {
            body = payload.Deserialize(PayslipJson.Default.Body);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            body = null;
        }
        var errors = new List<FieldError>();
        var paidOn = Date(body?.PaidOn, "payload.paidOn", errors, required: true);
        var effectiveOn = Date(body?.EffectiveOn, "payload.effectiveOn", errors, required: false);
        var net = Amount(body?.Net, "payload.net", errors);
        var gross = body?.Gross is null ? null : Amount(body.Gross, "payload.gross", errors);
        var taxable = body?.Taxable is null ? null : Amount(body.Taxable, "payload.taxable", errors);
        var label = body?.PhaseLabel?.Trim();
        if (body?.PhaseLabel is not null && label is not { Length: >= 1 and <= 100 })
            errors.Add(new FieldError("payload.phaseLabel", FieldErrors.ShapeCode, "1 to 100 characters."));
        return errors.Count > 0
            ? Result.Failure<Parsed>(FieldErrors.Shape(errors))
            : Result.Success(new Parsed(paidOn!.Value, label, net!.Value, gross, taxable, effectiveOn));
    }

    private static LocalDate? Date(string? text, string path, List<FieldError> errors, bool required)
    {
        if (text is null)
        {
            if (required)
                errors.Add(new FieldError(path, FieldErrors.ShapeCode, "An ISO date (yyyy-mm-dd) is required."));
            return null;
        }
        if (LocalDatePattern.Iso.Parse(text) is { Success: true } parsed)
            return parsed.Value;
        errors.Add(new FieldError(path, FieldErrors.ShapeCode, "An ISO date (yyyy-mm-dd) is required."));
        return null;
    }

    private static Money? Amount(AmountBody? body, string path, List<FieldError> errors)
    {
        if (!Currency.TryCreate(body?.Currency, out _))
        {
            errors.Add(new FieldError(path + ".currency", FieldErrors.ShapeCode, "A currency (ISO code) is required."));
            return null;
        }
        if (!Money.TryParse(body!.Amount, body.Currency, out var money) || money.Amount <= 0)
        {
            errors.Add(new FieldError(path + ".amount", FieldErrors.ShapeCode, "A positive decimal amount with a point (e.g. '2345.67') is required."));
            return null;
        }
        return money;
    }

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();

    private static Result<T> Field<T>(string code, string message, string path) where T : notnull
        => Result.Failure<T>(FieldErrors.At(new Failure(code, message, null), path));

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
