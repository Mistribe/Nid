using Nid.Core.Contracts;

namespace Nid.Subscriptions.Contracts;

public enum InsuranceKind
{
    Home,
    Car,
    Health,
    Provident,
    Other,
}

/// <summary>One guarantee of a policy. Ceiling and deductible are text the household typed: never parsed, never summed.</summary>
public sealed record InsuranceGuarantee(string Label, string? Ceiling, string? Deductible);

/// <summary>
/// What an insurance subscription covers. <paramref name="PropertyId"/> points at a property of the household
/// (checked by the caller: it has no foreign key here).
/// </summary>
public sealed record InsuranceDetails(
    InsuranceKind Kind, ItemId? PropertyId, string? InsuredLabel, string? PolicyNumber, string? InsurerContact,
    IReadOnlyList<InsuranceGuarantee> Guarantees);
