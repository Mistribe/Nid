using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Childcare.Application;

/// <summary>Spec §2, §1.10: the fields of a write, as field errors (422), and the loading every write shares.</summary>
public static class ChildcareCheck
{
    public const int NameMax = 120;
    public const int CodeMax = 40;

    public static async Task<Result<(Arrangement Arrangement, ItemAccess Item, IReadOnlyList<Terms> Terms)>> LoadAsync(
        IChildcareStore store, IItemRegistry items, ItemId id, CancellationToken ct)
    {
        var arrangement = await store.FindAsync(id, ct);
        var item = arrangement is null ? null : await items.FindAsync(id, ct);
        return arrangement is null || item is null
            ? Result.Failure<(Arrangement, ItemAccess, IReadOnlyList<Terms>)>(new NotFoundFailure("childcare", id.ToString()))
            : Result.Success((arrangement, item, await store.TermsOfAsync([id], ct)));
    }

    /// <summary>A closed arrangement refuses every write but its reopening.</summary>
    public static Result RequireOpen(ItemAccess item)
        => item.ArchivedAt is null ? Result.Success() : Result.Failure(new Failure(ChildcareCodes.Closed, "This arrangement is closed: reopen it first.", null));

    public static async Task<List<FieldError>> ChildAsync(IPersonRegistry persons, PersonId child, CancellationToken ct)
        => (await persons.FindAsync(child, ct))?.Kind == PersonKind.Dependent
            ? []
            : [new("childId", ChildcareCodes.ChildNotDependent, "The child is one of the household's dependents.")];

    public static List<FieldError> Name(string? name, string path = "name")
        => name is null || name.Trim().Length is < 1 or > NameMax ? [new(path, FieldErrors.ShapeCode, $"A name is 1 to {NameMax} characters.")] : [];

    public static List<FieldError> Carer(CarerInput carer)
    {
        var errors = Name(carer.Name, "carer.name");
        if (carer.Pajemploi?.Length > CodeMax) errors.Add(new("carer.pajemploi", FieldErrors.ShapeCode, $"At most {CodeMax} characters."));
        if (carer.Approval?.Length > CodeMax) errors.Add(new("carer.approval", FieldErrors.ShapeCode, $"At most {CodeMax} characters."));
        return errors;
    }

    public static List<FieldError> Terms(TermsInput terms)
    {
        var errors = new List<FieldError>();
        void Positive(decimal? value, string path, bool zero)
        {
            if (value is { } v && (zero ? v < 0 : v <= 0)) errors.Add(new($"terms.{path}", FieldErrors.ShapeCode, zero ? "Zero or more." : "More than zero."));
        }
        Positive(terms.NetHourlyRate, "netHourlyRate", zero: false);
        Positive(terms.GrossHourlyRate, "grossHourlyRate", zero: false);
        Positive(terms.OvertimeRate, "overtimePct", zero: true);
        Positive(terms.UpkeepPer9h, "upkeepPer9h", zero: true);
        Positive(terms.MealPrice, "mealPrice", zero: true);
        errors.AddRange(ContractCheck.WeekTypes(terms.WeekTypes).Select(p => new FieldError($"terms.{p.Field}", p.Code, Message(p.Code))));
        return errors;
    }

    /// <summary>The exceptions against the revisions as they will be after a write.</summary>
    public static List<FieldError> Overrides(IReadOnlyList<WeekOverride> overrides, IReadOnlyList<Terms> revisions)
        => ContractCheck.Overrides(overrides, revisions[0].From, revisions[^1].To, [.. revisions.Select(r => (r.From, r.WeekTypes.Count))])
            .Select(p => new FieldError(p.Field, p.Code, Message(p.Code))).ToList();

    public static List<FieldError> Gaps(NodaTime.LocalDate start, int weeks, IReadOnlyList<CareGap> gaps, NodaTime.LocalDate? since = null)
        => ContractCheck.Gaps(start, weeks, gaps, since).Select(p => new FieldError(p.Field, p.Code, Message(p.Code))).ToList();

    public static Result Fail(List<FieldError> errors) => errors.Count == 0 ? Result.Success() : Result.Failure(FieldErrors.Shape(errors));

    public static string Message(string code) => code switch
    {
        ChildcareCodes.ScheduleEmpty => "At least one day of care a week.",
        ChildcareCodes.ScheduleDuplicateDay => "One slot per day.",
        ChildcareCodes.DepartureBeforeArrival => "The departure comes after the arrival.",
        ChildcareCodes.WeeksOutOfRange => "1 to 46 programmed weeks: beyond, the year is complete.",
        ChildcareCodes.WeekTypesEmpty => "At least one week type.",
        ChildcareCodes.WeekTypeWeeksOutOfRange => "At least one week.",
        ChildcareCodes.WeekTypeNameInvalid => "A name of 1 to 40 characters, different from the other week types'.",
        ChildcareCodes.OverrideInvalidMonday => "A Monday within the arrangement's dates.",
        ChildcareCodes.OverrideUnknownWeekType => "This week type does not exist in the terms in force that week.",
        ChildcareCodes.TermsBothFormats => "Write week_types, or programmed_weeks with schedule, not both.",
        ChildcareCodes.GapNotMonday => "A week without care starts on a Monday.",
        ChildcareCodes.GapWeeksOutOfRange => "1 to 52 weeks.",
        ChildcareCodes.GapOverlap => "These weeks are already without care.",
        ChildcareCodes.GapsTooMany => "More weeks without care in a contract year than 52 less the programmed weeks.",
        _ => code,
    };
}
