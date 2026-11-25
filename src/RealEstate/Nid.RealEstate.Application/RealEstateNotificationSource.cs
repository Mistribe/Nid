using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

/// <summary>
/// Notifications design §2.3: the dashboard's loan and property events of the reader's own items, and the properties overview's
/// findings, as signals. No amount travels: a notification says what and when, the screen has the figures.
/// </summary>
public sealed class RealEstateNotificationSource(RealEstateUpcoming upcoming, PropertiesOverviewReader overview) : INotificationSource
{
    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    /// <summary>#645: the overview's findings, as notification kinds: <c>realestate.</c> and the watch's code.</summary>
    public const string RentArrears = "realestate.rent_arrears";
    public const string RevisionDue = "realestate.revision_due";
    public const string RevisionLost = "realestate.revision_lost";
    public const string IrlMissing = "realestate.irl_missing";
    public const string ValuationStale = "realestate.valuation_stale";

    public string Name => "realestate";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(UpcomingKinds.TenancyEnd, "due_date", NotificationLevel.Action, "realestate", 90)
        {
            Template = new("Un bail arrive à échéance") { Link = f => f.ItemPage("properties", "rental") },
        },
        new(UpcomingKinds.PropertyCostDue, "due_date", NotificationLevel.Info, "realestate", 7)
        {
            Template = new("Une charge prévue arrive") { Link = f => f.ItemPage("properties", "expenses") },
        },
        new(UpcomingKinds.PropertyTaxDue, "due_date", NotificationLevel.Action, "realestate", 14)
        {
            Template = new("La taxe foncière est à payer") { Link = f => f.ItemPage("properties", "expenses") },
        },
        new(UpcomingKinds.LoanPhaseEnd, "due_date", NotificationLevel.Info, "realestate", 30)
        {
            Template = new("Une phase de crédit se termine") { Link = f => f.ItemPage("loans") },
        },
        new(UpcomingKinds.LoanEnd, "due_date", NotificationLevel.Info, "realestate", 30)
        {
            Template = new("Un crédit se termine") { Link = f => f.ItemPage("loans") },
        },
        new(RentArrears, "finding", NotificationLevel.Action, "realestate", null)
        {
            Template = new("Des loyers sont impayés")
            {
                Suffix = f => f.KeyDate("yyyy-MM") is { } month ? $" — depuis {month.ToString("MMMM yyyy", NotificationFacts.French)}" : f.OnDueDate,
                Link = f => f.ItemPage("properties", "rental"),
            },
        },
        Revision(RevisionDue, NotificationLevel.Action, "Un loyer peut être révisé"),
        Revision(RevisionLost, NotificationLevel.Action, "Une révision de loyer a été perdue"),
        Revision(IrlMissing, NotificationLevel.Info, "L'IRL d'une révision manque"),
        new(ValuationStale, "finding", NotificationLevel.Info, "realestate", null)
        {
            Template = new("Une estimation est à refaire")
            {
                Suffix = f => (f.DueOn ?? f.KeyDate("yyyy-MM-dd")) is { } on ? $" — estimée le {NotificationFacts.Day(on)}" : "",
                Link = f => f.ItemPage("properties", "valuations"),
            },
        },
    ];

    /// <summary>A finding of the rental tab dated by its key: « — le 1er mars 2027 ».</summary>
    private static NotificationKind Revision(string kind, NotificationLevel level, string title)
        => new(kind, "finding", level, "realestate", null)
        {
            Template = new(title)
            {
                Suffix = f => (f.DueOn ?? f.KeyDate("yyyy-MM-dd")) is { } on ? $" — le {NotificationFacts.Day(on)}" : "",
                Link = f => f.ItemPage("properties", "rental"),
            },
        };

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();

        var events = await upcoming.UpcomingAsync(reader, VisibilityScope.Me, today, horizon, ct);
        if (!events.TryGetValue(out var list))
            return Result.Failure<IReadOnlyList<Signal>>(events.TryGetFailure(out var failure) ? failure! : new Failure("realestate.unknown", "The upcoming events could not be read.", null));
        foreach (var e in list!.Where(e => Kinds.Any(k => k.Kind == e.Kind && k.Family == "due_date")))
            signals.Add(new Signal(SignalKeys.Dated(e.Kind, e.Item, e.On), e.Kind, [e.Item], e.On, ParamsOf(e)));

        // The span only sizes the curve, which the watch does not read.
        var read = await overview.ReadAsync(reader, VisibilityScope.Me, PropertiesSpan.TwelveMonths, today, ct);
        if (!read.TryGetValue(out var view))
            return Result.Failure<IReadOnlyList<Signal>>(read.TryGetFailure(out var failure) ? failure! : new Failure("realestate.unknown", "The overview could not be read.", null));
        foreach (var watch in view!.Watch)
        {
            var kind = $"realestate.{watch.Code}";
            if (Kinds.All(k => k.Kind != kind || k.Family != "finding"))
                continue;
            var key = kind == RentArrears ? SignalKeys.Monthly(kind, watch.PropertyId, today)
                : watch.On is { } on ? SignalKeys.Dated(kind, watch.PropertyId, on)
                : SignalKeys.Undated(kind, watch.PropertyId);
            signals.Add(new Signal(key, kind, [watch.PropertyId], null, NoParams));
        }

        return Result.Success<IReadOnlyList<Signal>>(signals);
    }

    /// <summary>A lease's label or a cost's category code: what the sentence names, never an amount.</summary>
    private static IReadOnlyDictionary<string, string> ParamsOf(UpcomingEvent e) => e switch
    {
        { Kind: UpcomingKinds.TenancyEnd, Label: { Length: > 0 } label } => new Dictionary<string, string> { ["label"] = label },
        { Kind: UpcomingKinds.PropertyCostDue, Label: { Length: > 0 } category } => new Dictionary<string, string> { ["category"] = category },
        _ => NoParams,
    };
}
