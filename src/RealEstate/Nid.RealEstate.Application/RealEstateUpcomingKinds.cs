using Nid.Core.Contracts;

namespace Nid.RealEstate.Application;

/// <summary>Issue #514: the event kinds of the properties and loans (<see cref="RealEstateUpcoming"/>), 3E1 spec §4 and the tax calendar (#409).</summary>
public sealed class RealEstateUpcomingKinds : IUpcomingKinds
{
    public IReadOnlyList<UpcomingKind> Kinds { get; } =
    [
        new(UpcomingKinds.LoanPhaseEnd, UpcomingCadence.Milestone, label => $"Fin de la phase {label} : taux ou différé qui change"),
        new(UpcomingKinds.LoanEnd, UpcomingCadence.Milestone, _ => "Dernière échéance"),
        new(UpcomingKinds.ValuationDue, UpcomingCadence.Recurring, _ => "Estimation à refaire"),
        new(UpcomingKinds.PropertyCostDue, UpcomingCadence.Recurring, Category),
        new(UpcomingKinds.TenancyEnd, UpcomingCadence.Milestone, label => $"Fin du bail « {label} »"),
        new(UpcomingKinds.PropertyTaxDue, UpcomingCadence.Recurring, _ => "Taxe foncière à payer"),
        // « À faire », not « À venir »: never in the calendar feed (#716).
        new(UpcomingKinds.RentArrears, UpcomingCadence.Milestone, _ => null),
    ];

    // web/src/locales/fr.json, property_flows.category_option.
    private static string Category(string? code) => code switch
    {
        "works" => "Travaux",
        "construction_works" => "Construction, agrandissement",
        "property_tax" => "Taxe foncière",
        "condo_fees" => "Charges de copropriété",
        "insurance" => "Assurance",
        "energy" => "Énergie",
        "maintenance" => "Entretien",
        "management_fees" => "Frais de gestion",
        "other" => "Autre",
        _ => code ?? "",
    };
}
