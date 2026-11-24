using Nid.Core.Contracts;

namespace Nid.Investments.Application;

/// <summary>Issue #514: the event kinds of the envelopes (<see cref="InvestmentsUpcoming"/>); the label is the holding's name or the tax date's code.</summary>
public sealed class InvestmentsUpcomingKinds : IUpcomingKinds
{
    public IReadOnlyList<UpcomingKind> Kinds { get; } =
    [
        // Issue #34 §2.7.
        new(UpcomingKinds.HoldingMaturity, UpcomingCadence.Milestone, label => $"Échéance de « {label} » : vérifier où ira le capital"),
        // Issue #180: SCPI shares start earning after their entitlement delay.
        new(UpcomingKinds.HoldingEntitlement, UpcomingCadence.Milestone, label => $"Début de jouissance de « {label} » : premiers revenus à venir"),
        // Issue #400: an envelope's next tax date (8 years of a life insurance, 5 of a PEA, a PER's retirement); the label is its code.
        new(UpcomingKinds.TaxMilestone, UpcomingCadence.Milestone, label => label switch
        {
            "life_insurance_8y" => "8 ans du contrat : la fiscalité des retraits change",
            "pea_5y" => "5 ans du PEA : la fiscalité des retraits change",
            "per_retirement" => "Âge de la retraite : le PER peut être débloqué",
            _ => null,
        }),
    ];
}
