using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;

namespace Nid.Core.Tests;

/// <summary>
/// Issue #240: the calendar's timeline lists milestones only, so every event kind declares its cadence on the server. Issue #514: each module
/// declares its own kinds; the catalogue reads them all (every real kind: <c>UpcomingKindsTests</c> in the host's tests).
/// </summary>
public class UpcomingCadenceTests
{
    private sealed class Declared(params UpcomingKind[] kinds) : IUpcomingKinds
    {
        public IReadOnlyList<UpcomingKind> Kinds => kinds;
    }

    private static readonly UpcomingCatalog Catalog = new([
        new Declared(new UpcomingKind("loan_end", UpcomingCadence.Milestone, _ => "Dernière échéance")),
        new Declared(new UpcomingKind("subscription_renewal", UpcomingCadence.Recurring, label => label is null ? null : $"Renouvellement {label}")),
    ]);

    [Theory]
    [InlineData("loan_end", true)]
    [InlineData("subscription_renewal", false)]
    [InlineData("unknown", false)]
    public void A_milestone_happens_once(string kind, bool milestone) => Assert.Equal(milestone, Catalog.IsMilestone(kind));

    [Fact]
    public void A_kind_says_its_own_words_and_an_unknown_one_none()
    {
        Assert.Equal("Renouvellement A", Catalog.Summary("subscription_renewal", "A"));
        Assert.Null(Catalog.Summary("subscription_renewal", null));
        Assert.Null(Catalog.Summary("unknown", "A"));
    }

    [Fact]
    public void A_kind_declared_twice_is_a_wiring_bug()
    {
        var twice = new Declared(new UpcomingKind("loan_end", UpcomingCadence.Recurring, _ => null));

        var error = Assert.Throws<InvalidOperationException>(() => new UpcomingCatalog([twice, twice]));
        Assert.Contains("loan_end", error.Message);
    }
}
