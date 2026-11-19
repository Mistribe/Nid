using System.Reflection;
using Nid.Core.Contracts;

namespace Nid.Core.Tests;

/// <summary>Issue #240: the calendar's timeline lists milestones only, so every event kind declares its cadence on the server.</summary>
public class UpcomingCadenceTests
{
    private static IEnumerable<string> Kinds() => typeof(UpcomingKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_kind_declares_a_cadence()
    {
        Assert.NotEmpty(Kinds());
        Assert.All(Kinds(), kind => Assert.Contains(UpcomingKinds.Cadence[kind], new[] { UpcomingCadence.Recurring, UpcomingCadence.Milestone }));
        Assert.Equal(Kinds().Order(), UpcomingKinds.Cadence.Keys.Order());
    }

    [Theory]
    [InlineData(UpcomingKinds.LoanEnd, true)]
    [InlineData(UpcomingKinds.TaxMilestone, true)]
    [InlineData(UpcomingKinds.SubscriptionRenewal, false)]
    [InlineData(UpcomingKinds.TaxBalanceDue, false)]
    [InlineData("unknown", false)]
    public void A_milestone_happens_once(string kind, bool milestone) => Assert.Equal(milestone, UpcomingKinds.IsMilestone(kind));
}
