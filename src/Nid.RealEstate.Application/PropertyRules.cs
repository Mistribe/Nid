using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

/// <summary>
/// Spec 3C1 §2–§3: the bounds of a property's fields and of its flows. The first refusal wins; null when there is none.
/// Fields are the body's.
/// </summary>
internal static class PropertyRules
{
    public const int LocationLabelMax = 100;
    public const int StaleAfterDaysMax = 3650;

    /// <summary>1 to 100 characters after trimming, stored as sent (as a loan's name).</summary>
    public static Failure? Name(string? name)
        => name is null || name.Trim().Length is < 1 or > LoanRules.NameMax
            ? Invalid("name", $"A name is 1 to {LoanRules.NameMax} characters, and not only whitespace.")
            : null;

    /// <param name="rentalField">Where a rental start before the purchase answers: the field the request moved (precision 5).</param>
    public static Failure? Details(PropertyDetails d, LocalDate today, string rentalField = "rentalStartsOn")
    {
        if (d.PurchaseDate > today)
            return Invalid("purchaseDate", "A purchase is not dated after today.");
        if (d.PurchaseDate.Year is < LoanRules.FirstYear or > LoanRules.LastYear)
            return Invalid("purchaseDate", $"A purchase lies in years {LoanRules.FirstYear} to {LoanRules.LastYear}.");
        if ((Positive(d.PurchasePrice, "purchasePrice")
             ?? NotNegative(d.NotaryFees, "notaryFees")
             ?? NotNegative(d.AgencyFees, "agencyFees")
             ?? NotNegative(d.OtherAcquisitionCosts, "otherAcquisitionCosts")
             ?? (d.SurfaceM2 is { } surface ? Positive(surface, "surfaceM2") : null)) is { } amount)
            return amount;
        if (d.LocationLabel is { Length: > LocationLabelMax })
            return Invalid("locationLabel", $"A location has at most {LocationLabelMax} characters: never a full address.");
        if (d.StaleAfterDays is < 1 or > StaleAfterDaysMax)
            return Invalid("staleAfterDays", $"A staleness threshold is 1 to {StaleAfterDaysMax} days.");
        if (d.RentalStartsOn < d.PurchaseDate)
            return Invalid(rentalField, "A rental starts on or after the purchase.");
        return d.Note is { Length: > LoanRules.NoteMax } ? Invalid("note", $"A note has at most {LoanRules.NoteMax} characters.") : null;
    }

    /// <summary>Two decimals at most, below 10¹², above zero: a price, a surface, an estimate, a flow's amount (precision 2).</summary>
    public static Failure? Positive(decimal amount, string field)
        => Bounded(amount, field) ?? (amount <= 0m ? Invalid(field, "An amount is positive.") : null);

    private static Failure? NotNegative(decimal amount, string field)
        => Bounded(amount, field) ?? (amount < 0m ? Invalid(field, "An amount is not negative.") : null);

    /// <summary>The loans' bounds (two decimals, below 10¹²), under this module's property code.</summary>
    private static Failure? Bounded(decimal amount, string field)
        => LoanRules.Amount(amount, field) is { } refused ? Invalid(field, refused.Message) : null;

    /// <summary>README §4: a property's flow is an expense or an income, with a category of its kind (spec 3C1 §3).</summary>
    public static Failure? Category(string? kind, string? category)
    {
        var allowed = kind switch
        {
            "expense" => PropertyCategories.Expense,
            "income" => PropertyCategories.Income,
            _ => null,
        };
        if (allowed is null)
            return Refuse("kind", $"A property's flow is an expense or an income, not '{kind}'.");
        return category is not null && allowed.Contains(category)
            ? null
            : Refuse("category", $"This {kind}'s category is one of {string.Join(", ", allowed)}.");
    }

    /// <summary>Once <see cref="Category"/> has passed: the Core's kind.</summary>
    public static FlowKind KindOf(string kind) => kind == "income" ? FlowKind.Income : FlowKind.Expense;

    public static Failure Invalid(string field, string message)
        => new(PropertyCodes.InvalidPropertyField, message, new Dictionary<string, object?> { ["field"] = field });

    private static Failure Refuse(string field, string message)
        => new(PropertyCodes.InvalidCategory, message, new Dictionary<string, object?> { ["field"] = field });
}
