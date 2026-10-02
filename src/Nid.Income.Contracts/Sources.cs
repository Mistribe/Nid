using Nid.Core.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Contracts;

public enum IncomeKind { Salary, SelfEmployment, Pension, Unemployment, FamilyAllowance, SocialBenefit, Alimony, Other }

public static class IncomeWire
{
    public static string Of(IncomeKind kind) => kind switch
    {
        IncomeKind.Salary => "salary", IncomeKind.SelfEmployment => "self_employment", IncomeKind.Pension => "pension",
        IncomeKind.Unemployment => "unemployment", IncomeKind.FamilyAllowance => "family_allowance",
        IncomeKind.SocialBenefit => "social_benefit", IncomeKind.Alimony => "alimony", IncomeKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryKind(string? wire, out IncomeKind kind)
    {
        foreach (var k in Enum.GetValues<IncomeKind>())
            if (Of(k) == wire) { kind = k; return true; }
        kind = default;
        return false;
    }

    public const string OneOffCategory = "one_off";
}

/// <summary>The module's attributes of a source (spec §2); name, owners, visibility, currency and archive are the item's.</summary>
public sealed record IncomeSource(ItemId ItemId, IncomeKind Kind, string? PayerLabel, string? Note, LocalDate? EndedOn = null);

/// <summary>A phase (spec §2): half-open [StartsOn, EndsOn), every month(s) or one year, amounts in the source's currency.</summary>
public sealed record IncomePhase(
    Guid Id, ItemId Source, string Label, LocalDate StartsOn, LocalDate? EndsOn, Money NetAmount, Duration Every,
    Money? GrossAmount, int? MaxCycles, string? Note, uint Version = 0);
