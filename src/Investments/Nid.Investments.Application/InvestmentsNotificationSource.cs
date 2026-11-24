using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// Notifications design §2.3: the maturities of the reader's own holdings and the day their SCPI shares start earning (issue #180), and the overview's « value to update » and
/// « units of account under the floor » watch, as signals. No amount travels: a notification says what and when, the screen has the figures.
/// </summary>
public sealed class InvestmentsNotificationSource(InvestmentsUpcoming upcoming, InvestmentsOverviewReader overview) : INotificationSource
{
    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    public string Name => "investments";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(UpcomingKinds.HoldingMaturity, "due_date", NotificationLevel.Action, "investments", 30)
        {
            Template = new("Un support arrive à échéance") { Link = Holdings },
        },
        new(UpcomingKinds.HoldingEntitlement, "due_date", NotificationLevel.Info, "investments", 30)
        {
            Template = new("Des parts de SCPI entrent en jouissance") { Link = Holdings },
        },
        new(OverviewCodes.StaleValue, "finding", NotificationLevel.Info, "investments", null)
        {
            Template = new("Une valeur est à mettre à jour") { Suffix = f => Dated(f, "valeur du"), Link = f => f.ItemPage("investments") },
        },
        new(HoldingWarnings.UcShareBelowFloor, "finding", NotificationLevel.Info, "investments", null)
        {
            Template = new("La part en unités de compte est sous le plancher") { Suffix = f => Dated(f, "relevé du"), Link = Holdings },
        },
    ];

    private static string Holdings(NotificationFacts f) => f.ItemPage("investments", "holdings");

    /// <summary>« — valeur du 15 juin 2025 »: the watch's date is in its key.</summary>
    private static string Dated(NotificationFacts f, string words)
        => (f.DueOn ?? f.KeyDate("yyyy-MM-dd")) is { } on ? $" — {words} {NotificationFacts.Day(on)}" : "";

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();

        foreach (var e in await upcoming.MaturitiesAsync(reader, VisibilityScope.Me, today, horizon, ct))
            signals.Add(new Signal(SignalKeys.Dated(e.Kind, e.Item, e.On), e.Kind, [e.Item], e.On,
                e.Label is { Length: > 0 } label ? new Dictionary<string, string> { ["label"] = label } : NoParams));

        // The span only sizes the curve, which the watch does not read.
        var view = await overview.ReadAsync(reader, VisibilityScope.Me, OverviewSpan.TwelveMonths, today, ct);
        foreach (var watch in view.Watch.Where(w => w.Code is OverviewCodes.StaleValue or HoldingWarnings.UcShareBelowFloor))
            signals.Add(new Signal(
                watch.On is { } on ? SignalKeys.Dated(watch.Code, watch.EnvelopeId, on) : SignalKeys.Undated(watch.Code, watch.EnvelopeId),
                watch.Code, [watch.EnvelopeId], null, NoParams));

        return Result.Success<IReadOnlyList<Signal>>(signals);
    }
}
