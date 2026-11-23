using Nid.Core.Contracts;

namespace Nid.Tax.Application;

/// <summary>Issue #514: the event kinds of the PER deadline (<see cref="PerDeadline"/>) and of the yearly tax calendar (<see cref="TaxCalendarEvents"/>).</summary>
public sealed class TaxUpcomingKinds : IUpcomingKinds
{
    public IReadOnlyList<UpcomingKind> Kinds { get; } =
    [
        // Tax-per spec §4.6: on 31 December, the PER ceiling left to pay in (a person's, or the household's under pooling) and the part lost.
        new(UpcomingKinds.PerDeadline, UpcomingCadence.Recurring, _ => "PER : reste à verser avant le 31 décembre pour utiliser le plafond"),
        new(UpcomingKinds.PerCeilingExpiring, UpcomingCadence.Recurring, _ => "PER : minimum à verser avant le 31 décembre pour ne rien perdre"),
        // Issue #409.
        new(UpcomingKinds.TaxFilingDeadline, UpcomingCadence.Recurring, _ => "Impôt : date limite de la déclaration en ligne"),
        new(UpcomingKinds.TaxNewRate, UpcomingCadence.Recurring, _ => "Impôt : le nouveau taux de prélèvement s’applique"),
        new(UpcomingKinds.TaxBalanceDue, UpcomingCadence.Recurring, _ => "Impôt : prélèvement du solde"),
    ];
}
