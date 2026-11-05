using Nid.Investments.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Investments.Application;

/// <summary>2B1a spec §3.2 bounds, checked before anything is written.</summary>
internal static class EnvelopeRules
{
    public const int InstitutionMax = 100;
    public const int NoteMax = 2000;
    public const int NameMax = 100;

    /// <summary>
    /// Fix round 1, ruling 1: 1 to 100 characters after trimming, not only whitespace. The name is stored
    /// exactly as sent — this only decides whether it is accepted, and never trims what gets written.
    /// Fix round 2: the JSON body's <c>displayName</c> is bound with no required-parameter enforcement, so an
    /// omitted or explicit-null value reaches here as a real <c>null</c> — treated as an empty name (422), not
    /// a <see cref="NullReferenceException"/> (500).
    /// </summary>
    public static Result Name(string? name)
        => name is null || name.Trim().Length is < 1 or > NameMax
            ? Invalid("displayName", $"A name is 1 to {NameMax} characters, and not only whitespace.")
            : Result.Success();

    public static Result Check(string? institution, string? accountHint, int? staleAfterDays, string? note)
    {
        if (institution is { Length: > InstitutionMax })
            return Invalid("institution", $"An institution has at most {InstitutionMax} characters.");
        if (accountHint is { Length: < 1 or > 4 })
            return Invalid("accountHint", "An account hint is 1 to 4 characters: never the full number.");
        if (staleAfterDays is < 1 or > 3650)
            return Invalid("staleAfterDays", "A staleness threshold is 1 to 3650 days.");
        if (note is { Length: > NoteMax })
            return Invalid("note", $"A note has at most {NoteMax} characters.");
        return Result.Success();
    }

    /// <summary>Issue #34 §2.6: a percentage of the contract, 0 to 100, with 4 decimals at most.</summary>
    public static Result UcShareFloor(decimal? floor)
        => floor is { } f && (f is < 0 or > 100 || Math.Round(f, 4) != f)
            ? Invalid("ucShareFloorPct", "A units-of-account floor is 0 to 100 percent, with 4 decimals at most.")
            : Result.Success();

    /// <summary>Sale tax data: the premiums paid before 27/09/2017 that remain in a life insurance contract or a tontine, never negative.</summary>
    public static Result PremiumsBefore2017(decimal? premiums, EnvelopeKind kind)
        => premiums is null ? Result.Success()
            : kind is not (EnvelopeKind.LifeInsurance or EnvelopeKind.Tontine) ? Invalid("premiumsBefore2017", "Premiums before 27/09/2017 only apply to a life insurance contract or a tontine.")
            : premiums is < 0 || Math.Round(premiums.Value, 4) != premiums ? Invalid("premiumsBefore2017", "Premiums before 27/09/2017 are zero or more, with 4 decimals at most.")
            : Result.Success();

    /// <summary>Issue #399: a regulated livret is a savings envelope.</summary>
    public static Result Regulated(string? wire, EnvelopeKind kind, bool clear = false)
        => wire is null ? Result.Success()
            : clear ? Invalid("regulated", "A regulated livret is set or cleared, not both.")
            : !EnvelopeWire.TryRegulated(wire, out _) ? Invalid("regulated", $"'{wire}' is not a regulated livret.")
            : kind != EnvelopeKind.Savings ? Invalid("regulated", "Only a savings envelope is a regulated livret.")
            : Result.Success();

    public static RegulatedKind? ParseRegulated(string? wire) => EnvelopeWire.TryRegulated(wire, out var kind) ? kind : null;

    public static Result<EnvelopeKind> Kind(string? wire)
        => EnvelopeWire.TryKind(wire, out var kind) ? Result.Success(kind)
            : Result.Failure<EnvelopeKind>(new Failure(InvestmentsCodes.UnknownKind, $"'{wire}' is not an envelope kind.", null));

    public static Result<ValuationCadence> Cadence(string? wire)
        => wire is null ? Result.Success(ValuationCadence.Monthly)
            : EnvelopeWire.TryCadence(wire, out var cadence) ? Result.Success(cadence)
            : Result.Failure<ValuationCadence>(new Failure(InvestmentsCodes.UnknownCadence, $"'{wire}' is not a valuation cadence.", null));

    private static Result Invalid(string field, string message)
        => Result.Failure(new Failure(InvestmentsCodes.InvalidEnvelope, message, new Dictionary<string, object?> { ["field"] = field }));
}
