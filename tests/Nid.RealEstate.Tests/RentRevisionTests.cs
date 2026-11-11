using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

public sealed class RentRevisionTests
{
    private static readonly LocalDate Fetched = new(2026, 10, 23);
    private static readonly IReadOnlyList<IrlValue> Irl = IrlReader.Read(IrlTests.Vector())
        .Select(o => new IrlValue(o.Quarter, o.Value, o.PublishedOn, Fetched, "insee")).ToList();

    private static RevisionState State(LocalDate chainStart, decimal rent, LocalDate today, int? quarter = 2, bool indexed = true,
        bool revised = false, string? dpe = "C", IReadOnlyList<IrlValue>? irl = null)
        => RentRevision.State(chainStart, new Tenancy("Bail", chainStart, null, rent, 50m, null, null), indexed, quarter, revised, dpe, irl ?? Irl, today);

    [Fact]
    public void Anniversary_of_29_February_is_28_February_in_common_years()
    {
        Assert.Equal(new LocalDate(2025, 2, 28), RentRevision.Anniversary(new LocalDate(2024, 2, 29), 2025));
        Assert.Equal(new LocalDate(2028, 2, 29), RentRevision.Anniversary(new LocalDate(2024, 2, 29), 2028));
    }

    [Fact]
    public void Due_on_the_anniversary_with_the_quarter_published_by_then()
    {
        var due = Assert.IsType<RevisionDue>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1)));
        Assert.Equal(new IrlQuarter(2025, 2), due.From.Quarter);
        Assert.Equal(146.68m, due.From.Value);
        Assert.Equal(new IrlQuarter(2026, 2), due.To.Quarter);
        Assert.Equal(148.37m, due.To.Value);
        Assert.Equal(809.22m, due.NewRent);
        Assert.Equal(new LocalDate(2026, 9, 1), due.DefaultEffectiveOn);
        Assert.Equal(new LocalDate(2026, 9, 1), due.Anniversary);
        Assert.False(due.DpeUnknown);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 4)]
    [InlineData(4, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(10, 3)]
    [InlineData(12, 3)]
    public void An_indexed_lease_without_a_quarter_takes_the_last_one_over_at_its_start(int month, int quarter)
    {
        var lease = new Tenancy("Bail", new LocalDate(2026, month, 15), null, 800m, 50m, null, null, Indexed: true);
        Assert.Equal(quarter, RentRevision.WithDefaultQuarter(lease).IrlReferenceQuarter);
        Assert.Equal(2, RentRevision.WithDefaultQuarter(lease with { IrlReferenceQuarter = 2 }).IrlReferenceQuarter);
        Assert.Null(RentRevision.WithDefaultQuarter(lease with { Indexed = false }).IrlReferenceQuarter);
    }

    [Fact]
    public void Before_publication_the_previous_years_quarter_is_used()
    {
        var due = Assert.IsType<RevisionDue>(State(new LocalDate(2025, 7, 1), 800m, new LocalDate(2026, 7, 1)));
        Assert.Equal(new IrlQuarter(2025, 2), due.To.Quarter);
        Assert.Equal(new IrlQuarter(2024, 2), due.From.Quarter);
        Assert.Equal(145.17m, due.From.Value);
        Assert.Equal(808.32m, due.NewRent);
    }

    [Fact]
    public void A_second_revision_compounds()
    {
        var irl = Irl.Append(new IrlValue(new IrlQuarter(2027, 2), 150.00m, null, new LocalDate(2027, 8, 1), "manual")).ToList();
        var due = Assert.IsType<RevisionDue>(State(new LocalDate(2025, 9, 1), 809.22m, new LocalDate(2027, 9, 1), irl: irl));
        Assert.Equal(Math.Round(809.22m * 150.00m / 148.37m, 2, MidpointRounding.AwayFromZero), due.NewRent);
        Assert.Equal(148.37m, due.From.Value);
    }

    [Fact]
    public void Missing_quarter_gives_IrlMissing()
    {
        var missing = Assert.IsType<RevisionIrlMissing>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2027, 9, 1)));
        Assert.Equal(new IrlQuarter(2027, 2), missing.Missing);
        Assert.Equal(new LocalDate(2027, 9, 1), missing.Anniversary);
    }

    [Fact]
    public void A_T4_lease_with_a_January_anniversary_takes_the_quarter_published_by_then()
    {
        IrlValue Q4(int year, decimal value) => new(new IrlQuarter(year, 4), value, new LocalDate(year + 1, 1, 15), new LocalDate(year + 1, 1, 15), "insee");
        var irl = new[] { Q4(2024, 144.64m), Q4(2025, 145.47m), Q4(2026, 147.00m) };
        var due = Assert.IsType<RevisionDue>(State(new LocalDate(2026, 1, 5), 800m, new LocalDate(2027, 1, 20), quarter: 4, irl: irl));
        Assert.Equal(new IrlQuarter(2025, 4), due.To.Quarter);
        Assert.Equal(new IrlQuarter(2024, 4), due.From.Quarter);
    }

    [Fact]
    public void A_quarter_over_but_not_stored_gives_IrlMissing()
    {
        var irl = Irl.Where(v => v.Quarter != new IrlQuarter(2026, 2)).ToList();
        Assert.Equal(new IrlQuarter(2026, 2), Assert.IsType<RevisionIrlMissing>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1), irl: irl)).Missing);
    }

    [Fact]
    public void A_quarter_published_after_the_anniversary_gives_the_previous_year()
    {
        var irl = Irl.Select(v => v.Quarter == new IrlQuarter(2026, 2) ? v with { PublishedOn = new LocalDate(2026, 9, 2) } : v).ToList();
        Assert.Equal(new IrlQuarter(2025, 2), Assert.IsType<RevisionDue>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 5), irl: irl)).To.Quarter);
    }

    [Fact]
    public void A_value_entered_after_the_anniversary_counts_from_its_publication()
    {
        var irl = Irl.Select(v => v.Quarter == new IrlQuarter(2026, 2) ? v with { FetchedOn = new LocalDate(2026, 10, 1), Source = "manual" } : v).ToList();
        Assert.Equal(new IrlQuarter(2026, 2), Assert.IsType<RevisionDue>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 10, 1), irl: irl)).To.Quarter);
    }

    [Fact]
    public void Not_indexed_or_before_first_anniversary_gives_None()
    {
        Assert.IsType<RevisionNone>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1), indexed: false));
        Assert.IsType<RevisionNone>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1), quarter: null));
        Assert.IsType<RevisionNone>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 8, 31)));
    }

    [Fact]
    public void Already_revised_this_anniversary_gives_None()
        => Assert.IsType<RevisionNone>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1), revised: true));

    [Theory]
    [InlineData("F")]
    [InlineData("G")]
    public void DPE_F_or_G_forbids(string dpe)
        => Assert.Equal(new RevisionForbiddenDpe(new LocalDate(2026, 9, 1)), State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1), dpe: dpe));

    [Fact]
    public void DPE_unknown_is_due_with_the_flag()
        => Assert.True(Assert.IsType<RevisionDue>(State(new LocalDate(2025, 9, 1), 800m, new LocalDate(2026, 9, 1), dpe: null)).DpeUnknown);

    [Fact]
    public void Window_closes_one_year_after_the_anniversary()
    {
        var chain = new LocalDate(2025, 9, 1);
        Assert.Equal(new LocalDate(2026, 9, 1), Assert.IsType<RevisionDue>(State(chain, 800m, new LocalDate(2027, 8, 31))).Anniversary);
        // On 2027-09-01 the next window opens: its quarter is not stored, the 2026 one is no longer offered.
        Assert.IsType<RevisionIrlMissing>(State(chain, 800m, new LocalDate(2027, 9, 1)));
    }

    [Fact]
    public void Lost_reports_the_previous_unrevised_anniversary()
    {
        var chain = new LocalDate(2024, 9, 1);
        Assert.Equal(new LocalDate(2025, 9, 1), RentRevision.Lost(chain, [], "C", new LocalDate(2026, 10, 1)));
        Assert.Null(RentRevision.Lost(chain, [new LocalDate(2025, 9, 15)], "C", new LocalDate(2026, 10, 1)));
        // 2025-09-01 is the first anniversary: nothing before it can be lost.
        Assert.Null(RentRevision.Lost(chain, [], "C", new LocalDate(2025, 10, 1)));
        Assert.Null(RentRevision.Lost(chain, [], "C", new LocalDate(2025, 8, 1)));
    }

    [Theory]
    [InlineData("F")]
    [InlineData("G")]
    public void A_forbidden_revision_is_not_lost(string dpe)
        => Assert.Null(RentRevision.Lost(new LocalDate(2024, 9, 1), [], dpe, new LocalDate(2026, 10, 1)));

    [Fact]
    public void Revising_at_the_current_anniversary_clears_the_lost_one()
        => Assert.Null(RentRevision.Lost(new LocalDate(2024, 9, 1), [new LocalDate(2026, 9, 1)], "C", new LocalDate(2026, 10, 1)));

    [Fact]
    public void Effective_date_between_anniversary_and_today_on_or_before_ends_on()
    {
        var anniversary = new LocalDate(2026, 9, 1);
        var today = new LocalDate(2026, 9, 20);
        Assert.True(RentRevision.EffectiveOnAllowed(anniversary, anniversary, today, null));
        Assert.True(RentRevision.EffectiveOnAllowed(today, anniversary, today, new LocalDate(2026, 9, 21)));
        Assert.False(RentRevision.EffectiveOnAllowed(new LocalDate(2026, 8, 31), anniversary, today, null));
        Assert.False(RentRevision.EffectiveOnAllowed(new LocalDate(2026, 9, 21), anniversary, today, null));
        Assert.True(RentRevision.EffectiveOnAllowed(today, anniversary, today, today));
        Assert.False(RentRevision.EffectiveOnAllowed(today, anniversary, today, today.PlusDays(-1)));
    }
}
