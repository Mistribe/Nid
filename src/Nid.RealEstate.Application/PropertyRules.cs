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

    /// <summary>Spec 3C2 §2: a planned cost comes every 1 to 120 months or years.</summary>
    public const int EveryCountMax = 120;

    /// <summary>Spec 3C2 §2: an expense category of the property without <c>works</c>, already in the cost price, nor <c>management_fees</c> (not in the table's check).</summary>
    public static readonly IReadOnlyList<string> RecurringCategories = PropertyCategories.Expense.Where(c => c is not (PropertyCategories.Works or PropertyCategories.ManagementFees)).ToList();

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
        if (d.EnergyClass is not null && !EnergyClasses.Contains(d.EnergyClass))
            return Invalid("energyClass", "An energy class (DPE) is a letter from A to G.");
        return d.Note is { Length: > LoanRules.NoteMax } ? Invalid("note", $"A note has at most {LoanRules.NoteMax} characters.") : null;
    }

    /// <summary>Rental spec §3.2: the DPE classes.</summary>
    public static readonly IReadOnlyList<string> EnergyClasses = ["A", "B", "C", "D", "E", "F", "G"];

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

    /// <summary>Spec 3C2 §2: a planned cost, at the body's fields (<c>every.count</c>, <c>every.unit</c> for its period).</summary>
    public static Failure? CostFields(RecurringCostDraft c)
    {
        if (c.Category is null || !RecurringCategories.Contains(c.Category))
            return Refuse("category", $"A recurring cost's category is one of {string.Join(", ", RecurringCategories)}.");
        if (Positive(c.Amount, "amount") is { } amount)
            return amount;
        if (c.EveryCount is < 1 or > EveryCountMax)
            return Invalid("every.count", $"A recurring cost comes every 1 to {EveryCountMax} months or years.");
        if (c.EveryUnit is not (DurationUnit.Month or DurationUnit.Year))
            return Invalid("every.unit", "A recurring cost comes every month(s) or year(s).");
        return InYears(c.StartsOn, "startsOn") ?? InYears(c.EndsOn, "endsOn") ?? Ends(c.StartsOn, c.EndsOn) ?? Note(c.Note);
    }

    /// <summary>Spec 3C2 §2: a tenancy's label.</summary>
    public const int TenancyLabelMax = 100;

    /// <summary>Spec 3C2 §2: a tenancy alone, against its property's purchase; the overlap with the others is <see cref="Overlap"/>'s.</summary>
    public static Failure? TenancyFields(Tenancy t, LocalDate purchaseDate)
    {
        if (t.Label.Trim().Length is < 1 or > TenancyLabelMax)
            return Invalid("label", $"A label is 1 to {TenancyLabelMax} characters, and not only whitespace: never personal data.");
        if ((InYears(t.StartsOn, "startsOn") ?? InYears(t.EndsOn, "endsOn")) is { } year)
            return year;
        if (t.StartsOn < purchaseDate)
            return Invalid("startsOn", "A tenancy starts on or after the purchase.");
        return Ends(t.StartsOn, t.EndsOn)
               ?? Positive(t.RentMonthly, "rentMonthly")
               ?? NotNegative(t.ChargesMonthly, "chargesMonthly")
               ?? (t.Deposit is { } deposit ? Positive(deposit, "deposit") : null)
               ?? Quarter(t)
               ?? Note(t.Note);
    }

    /// <summary>
    /// Spec 3C2 §2: two tenancies of one property never overlap, each from its start (inclusive) to its end (exclusive), an open one
    /// running on; <paramref name="others"/> leaves out the one being written.
    /// </summary>
    public static Failure? Overlap(Tenancy t, IEnumerable<Tenancy> others)
        => others.Any(o => o.StartsOn < (t.EndsOn ?? LocalDate.MaxIsoValue) && t.StartsOn < (o.EndsOn ?? LocalDate.MaxIsoValue))
            ? new Failure(PropertyCodes.TenancyOverlap, "Another tenancy of this property covers part of this period.",
                new Dictionary<string, object?> { ["field"] = "startsOn" })
            : null;

    /// <summary>Rental spec §3.2: a quarter is 1 to 4; an indexed tenancy has one. A quarter without <c>indexed</c> is kept.</summary>
    private static Failure? Quarter(Tenancy t)
        => t.IrlReferenceQuarter is < 1 or > 4 || (t.Indexed && t.IrlReferenceQuarter is null)
            ? Invalid("irlReferenceQuarter", "An indexed tenancy names its IRL reference quarter, 1 to 4.")
            : null;

    /// <summary>A range is half-open: it ends after it starts.</summary>
    private static Failure? Ends(LocalDate startsOn, LocalDate? endsOn)
        => endsOn <= startsOn ? Invalid("endsOn", "It ends after it starts.") : null;

    /// <summary>The loans' years (1900–2200), under this module's property code.</summary>
    private static Failure? InYears(LocalDate? date, string field)
        => LoanRules.InYears(date, field) is { } refused ? Invalid(field, refused.Message) : null;

    private static Failure? Note(string? note)
        => note is { Length: > LoanRules.NoteMax } ? Invalid("note", $"A note has at most {LoanRules.NoteMax} characters.") : null;

    /// <summary>Once <see cref="Category"/> has passed: the Core's kind.</summary>
    public static FlowKind KindOf(string kind) => kind == "income" ? FlowKind.Income : FlowKind.Expense;

    public static Failure Invalid(string field, string message)
        => new(PropertyCodes.InvalidPropertyField, message, new Dictionary<string, object?> { ["field"] = field });

    private static Failure Refuse(string field, string message)
        => new(PropertyCodes.InvalidCategory, message, new Dictionary<string, object?> { ["field"] = field });
}
