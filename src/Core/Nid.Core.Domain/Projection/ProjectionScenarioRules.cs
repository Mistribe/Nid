using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Projection;

/// <summary>
/// Projection spec §4 and §6: the shape of a scenario document (what the writer can get wrong on its own; whether the items exist
/// and are visible is checked against the data) and the date of an event.
/// </summary>
public static class ProjectionScenarioRules
{
    public const int Version = 1;
    public const int MaxYears = 40;
    public const int MaxEvents = 30;
    public const int MinAge = 18;
    public const int MaxAge = 100;

    private static readonly HashSet<string> Kinds =
    [
        ProjectionEventKinds.IncomeChange, ProjectionEventKinds.SavingsChange, ProjectionEventKinds.EarlyRepayment,
        ProjectionEventKinds.PropertySale, ProjectionEventKinds.PropertyPurchase,
    ];

    /// <summary>The refusals of a document, as a field path and a message; empty when its shape is valid.</summary>
    public static IReadOnlyList<(string Field, string Message)> Check(ProjectionScenario doc)
    {
        var refusals = new List<(string Field, string Message)>();
        void Refuse(string field, string message) => refusals.Add((field, message));

        if (doc.Version != Version)
            Refuse("version", $"The version must be {Version}.");

        if (doc.Horizon is not { } horizon || (horizon.Years is null) == (horizon.RetireOf is null)
            || horizon.Years is { } years && years is < 1 or > MaxYears)
            Refuse("horizon", $"The horizon is a number of years between 1 and {MaxYears}, or the retirement of a person, not both.");

        var events = doc.Events ?? [];
        if (events.Count > MaxEvents)
            Refuse("events", $"A scenario holds at most {MaxEvents} events.");

        var ids = new HashSet<Guid>();
        var sold = new HashSet<ItemId>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var at = $"events[{i}]";
            if (!ids.Add(e.Id))
                Refuse($"{at}.id", "Two events share the same id.");
            CheckDate(e.Start, $"{at}.start", Refuse);
            if (e.Until is { } until)
                CheckDate(until, $"{at}.until", Refuse);
            CheckKind(e, at, Refuse);
            if (e is { Kind: ProjectionEventKinds.PropertySale, Property: { } property } && !sold.Add(property))
                Refuse($"{at}.property", "A property is sold once.");
            if (e.Until is { On: { } untilOn } && e.Start is { On: { } startOn } && untilOn <= startOn)
                Refuse($"{at}.until", "The end must be after the start.");
        }

        var seen = new HashSet<PersonId>();
        var retirements = doc.Retirements ?? [];
        for (var i = 0; i < retirements.Count; i++)
        {
            var r = retirements[i];
            var at = $"retirements[{i}]";
            if (!seen.Add(r.Person))
                Refuse($"{at}.person", "A person has one departure per scenario.");
            if (r.Years is < 50 or > 75)
                Refuse($"{at}.years", "The age is between 50 and 75.");
            if (r.Months is < 0 or > 11)
                Refuse($"{at}.months", "The months are between 0 and 11.");
            if (r.MonthlyPension < 0)
                Refuse($"{at}.monthlyPension", "The pension is not negative.");
        }

        return refusals;
    }

    private static void CheckDate(EventDate? date, string at, Action<string, string> refuse)
    {
        if (date is null)
        {
            refuse(at, "A date is required.");
            return;
        }

        var hasOn = date.On is not null;
        var hasAge = date.Person is not null && date.Age is not null;
        if (hasOn == hasAge || (date.Person is null) != (date.Age is null))
            refuse(at, "A date is either a day, or a person with an age.");
        else if (date.Age is { } age && age is < MinAge or > MaxAge)
            refuse($"{at}.age", $"The age is between {MinAge} and {MaxAge}.");
    }

    private static void CheckKind(ProjectionEvent e, string at, Action<string, string> refuse)
    {
        if (!Kinds.Contains(e.Kind))
        {
            refuse($"{at}.kind", "Unknown kind of event.");
            return;
        }

        var change = e.Kind is ProjectionEventKinds.IncomeChange or ProjectionEventKinds.SavingsChange;
        var repayment = e.Kind == ProjectionEventKinds.EarlyRepayment;
        var sale = e.Kind == ProjectionEventKinds.PropertySale;
        var purchase = e.Kind == ProjectionEventKinds.PropertyPurchase;

        void Required(bool present, string field, string message)
        {
            if (!present)
                refuse($"{at}.{field}", message);
        }

        void Foreign(bool present, bool belongs, string field)
        {
            if (present && !belongs)
                refuse($"{at}.{field}", "This field does not belong to this kind of event.");
        }

        Foreign(e.Until is not null, change, "until");
        Foreign(e.MonthlyAmount is not null, change, "monthlyAmount");
        Foreign(e.Amount is not null, repayment, "amount");
        Foreign(e.Split is not null, repayment, "split");
        Foreign(e.Effect is not null, repayment, "effect");
        Foreign(e.Penalty is not null, repayment, "penalty");
        Foreign(e.Property is not null, sale, "property");
        Foreign(e.Price is not null, sale, "price");
        Foreign(e.Costs is not null, sale, "costs");
        Foreign(e.Scenario is not null, purchase, "scenario");
        // A loan is read only by a repayment that chose it; for any other kind it is foreign, for a repayment the split decides.
        Foreign(e.Loan is not null, repayment, "loan");

        if (change)
            Required(e.MonthlyAmount is not null, "monthlyAmount", "The monthly amount is required.");
        else if (repayment)
        {
            Required(e.Amount > 0, "amount", "The amount is required and positive.");
            Required(e.Split is not null && ProjectionSplits.All.Contains(e.Split), "split", "Unknown split.");
            if (e.Split == ProjectionSplits.Chosen)
                Required(e.Loan is not null, "loan", "The loan is required when the split is chosen.");
            else if (e.Loan is not null && e.Split is not null)
                refuse($"{at}.loan", "The loan applies only to a chosen split.");
            Required(e.Effect is not null && ProjectionEffects.All.Contains(e.Effect), "effect", "Unknown effect.");
            Required(e.Penalty >= 0, "penalty", "The penalty is required and not negative.");
        }
        else if (sale)
        {
            Required(e.Property is not null, "property", "The property is required.");
            Required(e.Costs >= 0, "costs", "The costs are required and not negative.");
            Required(e.Price is null or >= 0, "price", "The price is not negative.");
        }
        else
            Required(e.Scenario is not null, "scenario", "The purchase scenario is required.");
    }

    /// <summary>
    /// The day of an event date: <c>On</c> as is; at an age, the last day of the month the person reaches it. <c>null</c> when the
    /// person or their birth date is unknown.
    /// </summary>
    public static LocalDate? Resolve(EventDate date, Func<PersonId, PersonInfo?> person)
    {
        if (date.On is { } on)
            return on;
        if (date.Person is not { } id || date.Age is not { } age || person(id)?.BirthDate is not { } birth)
            return null;
        var month = birth.PlusYears(age);
        return new LocalDate(month.Year, month.Month, 1).PlusMonths(1).PlusDays(-1);
    }
}
