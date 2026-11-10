using NodaTime;
using Nid.Core.Contracts;

namespace Nid.Core.Tests;

/// <summary>Retirement spec §1: the departure month, and the month the projection switches a person's work income off.</summary>
public class DeparturesTests
{
    private static readonly LocalDate Today = new(2026, 11, 10);

    private static PersonInfo Adult(PersonId id, LocalDate? born, int age, int months = 0)
        => new(id, PersonKind.Adult, "Alice", null, 0, BirthDate: born, RetirementAge: age, RetirementMonths: months);

    [Fact]
    public void MonthOf_adds_years_and_months()
        => Assert.Equal(new YearMonth(2053, 2), Departures.MonthOf(Adult(PersonId.New(), new LocalDate(1990, 5, 10), 62, 9)));

    [Fact]
    public void MonthOf_is_null_without_birth_date()
        => Assert.Null(Departures.MonthOf(Adult(PersonId.New(), null, 62)));

    [Fact]
    public void SwitchMonths_is_the_month_after_the_departure()
    {
        var a = Adult(PersonId.New(), new LocalDate(1964, 11, 20), 62);
        var b = Adult(PersonId.New(), new LocalDate(1990, 5, 10), 64);
        var switches = Departures.SwitchMonths([a, b], Today);
        Assert.Equal(1, switches[a.Id]);
        Assert.Equal((2054 - 2026) * 12 + 5 - 11 + 1, switches[b.Id]);
    }

    [Fact]
    public void SwitchMonths_keeps_a_past_departure_below_one()
    {
        var a = Adult(PersonId.New(), new LocalDate(1950, 1, 1), 64);
        // Departure 2014-01: (2014 - 2026) * 12 + 1 - 11 + 1.
        Assert.Equal(-153, Departures.SwitchMonths([a], Today)[a.Id]);
    }

    [Fact]
    public void SwitchMonths_skips_dependents_and_missing_birth_dates()
    {
        var kid = new PersonInfo(PersonId.New(), PersonKind.Dependent, "Léa", null, 0, BirthDate: new LocalDate(2015, 1, 1));
        var noBirth = Adult(PersonId.New(), null, 64);
        Assert.Empty(Departures.SwitchMonths([kid, noBirth], Today));
    }

    [Fact]
    public void SwitchOf_is_the_latest_and_null_when_an_owner_has_none()
    {
        PersonId a = PersonId.New(), b = PersonId.New(), c = PersonId.New();
        var switches = new Dictionary<PersonId, int> { [a] = 10, [b] = 20 };
        Assert.Equal(20, Departures.SwitchOf([a, b], switches));
        Assert.Null(Departures.SwitchOf([a, c], switches));
        Assert.Null(Departures.SwitchOf([], switches));
    }
}
