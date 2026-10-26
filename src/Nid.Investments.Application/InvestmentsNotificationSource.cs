using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Investments.Application;

/// <summary>
/// Notifications design §2.3: the maturities of the reader's own holdings, and the overview's « value to update » and
/// « units of account under the floor » watch, as signals. No amount travels: a notification says what and when, the screen has the figures.
/// </summary>
public sealed class InvestmentsNotificationSource(InvestmentsUpcoming upcoming, InvestmentsOverviewReader overview) : INotificationSource
{
    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    public string Name => "investments";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(UpcomingKinds.HoldingMaturity, "due_date", NotificationLevel.Action, "investments", 30),
        new(OverviewCodes.StaleValue, "finding", NotificationLevel.Info, "investments", null),
        new(HoldingWarnings.UcShareBelowFloor, "finding", NotificationLevel.Info, "investments", null),
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();

        var events = await upcoming.UpcomingAsync(reader, VisibilityScope.Me, today, horizon, ct);
        if (!events.TryGetValue(out var list))
            return Result.Failure<IReadOnlyList<Signal>>(events.TryGetFailure(out var failure) ? failure! : new Failure("investments.unknown", "The upcoming events could not be read.", null));
        foreach (var e in list!.Where(e => e.Kind == UpcomingKinds.HoldingMaturity))
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
