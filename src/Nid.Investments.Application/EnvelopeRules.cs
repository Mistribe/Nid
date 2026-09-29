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
