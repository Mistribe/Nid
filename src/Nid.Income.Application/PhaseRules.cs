using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Domain;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Application;

/// <summary>2C1 spec §2 rules of a phase; the API's creation, the phase routes (Task 3) and the import (Task 5) share them.</summary>
internal static class PhaseRules
{
    public const int LabelMax = 60;
    public const int NoteMax = 2000;

    /// <summary>100 years of monthly occurrences: far beyond any real contract, and a bound the dates cannot overflow.</summary>
    public const int MaxCyclesMax = 1200;

    /// <summary>Spec §2: one phase alone. Fields are the API's (label, startsOn, endsOn, every, grossAmount, taxableAmount, maxCycles, note).</summary>
    public static Result Check(string? label, LocalDate startsOn, LocalDate? endsOn, Money net, Duration every, Money? gross, Money? taxable, int? maxCycles, string? note, Currency currency)
    {
        if (label is null || label.Trim().Length is < 1 or > LabelMax)
            return Invalid(IncomeCodes.InvalidPhase, "label", $"A label is 1 to {LabelMax} characters, and not only whitespace.");
        if (endsOn is { } end && end <= startsOn)
            return Invalid(IncomeCodes.InvalidPhase, "endsOn", "A phase ends after it starts.");
        if (!(every.Unit == DurationUnit.Month && every.Count is >= 1 and <= 12 || every.Unit == DurationUnit.Year && every.Count == 1))
            return Invalid(IncomeCodes.UnknownCadence, "every", "A phase comes every 1 to 12 months, or every year.");
        if (net.Amount <= 0)
            return Invalid(CoreFactCodes.AmountNotPositive, "netAmount", "A net amount is more than zero.");
        if (gross is { Amount: <= 0 })
            return Invalid(CoreFactCodes.AmountNotPositive, "grossAmount", "A gross amount is more than zero.");
        if (net.Currency != currency || gross is { } g && g.Currency != currency)
            return Invalid(CoreFactCodes.CurrencyMismatch, net.Currency != currency ? "netAmount" : "grossAmount", $"This source's amounts are in {currency.Code}.");
        // Tax-per spec §3.1: the net taxable income is positive, in the source's currency, and no more than the gross when known.
        if (taxable is { Amount: <= 0 })
            return Invalid(IncomeCodes.InvalidTaxable, "taxableAmount", "A net taxable amount is more than zero.");
        if (taxable is { } t && t.Currency != currency)
            return Invalid(CoreFactCodes.CurrencyMismatch, "taxableAmount", $"This source's amounts are in {currency.Code}.");
        if (taxable is { } tx && gross is { } gr && tx.Amount > gr.Amount)
            return Invalid(IncomeCodes.InvalidTaxable, "taxableAmount", "A net taxable amount is at most the gross.");
        if (maxCycles is < 1 or > MaxCyclesMax)
            return Invalid(IncomeCodes.InvalidPhase, "maxCycles", $"A phase has 1 to {MaxCyclesMax} occurrences.");
        if (note is { Length: > NoteMax })
            return Invalid(IncomeCodes.InvalidPhase, "note", $"A note has at most {NoteMax} characters.");
        return Result.Success();
    }

    /// <summary>
    /// Spec §2: no two phases of one label overlap, each taken up to its effective end (a capped phase stops after its last
    /// occurrence); <paramref name="others"/> excludes the phase itself.
    /// </summary>
    public static Result NoOverlap(string label, LocalDate startsOn, LocalDate? endsOn, Duration every, int? maxCycles, IEnumerable<IncomePhase> others)
    {
        var end = IncomeMath.EffectiveEnd(startsOn, endsOn, every, maxCycles) ?? LocalDate.MaxIsoValue;
        return others.Any(o => o.Label == label && o.StartsOn < end && startsOn < (IncomeMath.EffectiveEnd(o) ?? LocalDate.MaxIsoValue))
            ? Invalid(IncomeCodes.PhaseOverlap, "startsOn", $"Another '{label}' phase covers part of this period.")
            : Result.Success();
    }

    /// <summary>The same failure, its field under <paramref name="prefix"/> (<c>phases[1].startsOn</c>): a phase of a larger body.</summary>
    public static Failure Under(Failure failure, string prefix)
        => failure.Metadata is { } metadata && metadata.TryGetValue("field", out var field) && field is string name
            ? new Failure(failure.Code, failure.Message, new Dictionary<string, object?>(metadata) { ["field"] = $"{prefix}.{name}" })
            : failure;

    private static Result Invalid(string code, string field, string message)
        => Result.Failure(new Failure(code, message, new Dictionary<string, object?> { ["field"] = field }));
}
