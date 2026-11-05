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

    public string Name => "realestate";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(UpcomingKinds.TenancyEnd, "due_date", NotificationLevel.Action, "properties", 90),
        new(UpcomingKinds.PropertyCostDue, "due_date", NotificationLevel.Info, "properties", 7),
        new(UpcomingKinds.PropertyTaxDue, "due_date", NotificationLevel.Action, "properties", 14),
        new(UpcomingKinds.LoanPhaseEnd, "due_date", NotificationLevel.Info, "loans", 30),
        new(UpcomingKinds.LoanEnd, "due_date", NotificationLevel.Info, "loans", 30),
        new(PropertyOverviewCodes.RentArrears, "finding", NotificationLevel.Action, "properties", null),
        new(PropertyOverviewCodes.RevisionDue, "finding", NotificationLevel.Action, "properties", null),
        new(PropertyOverviewCodes.RevisionLost, "finding", NotificationLevel.Action, "properties", null),
        new(PropertyOverviewCodes.IrlMissing, "finding", NotificationLevel.Info, "properties", null),
        new(PropertyWarnings.ValuationStale, "finding", NotificationLevel.Info, "properties", null),
    ];

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
            if (Kinds.All(k => k.Kind != watch.Code || k.Family != "finding"))
                continue;
            var key = watch.Code == PropertyOverviewCodes.RentArrears ? SignalKeys.Monthly(watch.Code, watch.PropertyId, today)
                : watch.On is { } on ? SignalKeys.Dated(watch.Code, watch.PropertyId, on)
                : SignalKeys.Undated(watch.Code, watch.PropertyId);
            signals.Add(new Signal(key, watch.Code, [watch.PropertyId], null, NoParams));
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
