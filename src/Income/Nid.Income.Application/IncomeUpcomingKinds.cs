using Nid.Core.Contracts;

namespace Nid.Income.Application;

/// <summary>Issue #514: the event kinds of the income sources (<see cref="IncomeSourcesUpcoming"/>); the label is the phase's.</summary>
public sealed class IncomeUpcomingKinds : IUpcomingKinds
{
    public IReadOnlyList<UpcomingKind> Kinds { get; } =
    [
        new(UpcomingKinds.IncomePhaseEnd, UpcomingCadence.Milestone, label => $"Fin de « {label} »"),
        // « À faire », not « À venir »: never in the calendar feed.
        new(UpcomingKinds.IncomeGapSoon, UpcomingCadence.Milestone, _ => null),
    ];
}
