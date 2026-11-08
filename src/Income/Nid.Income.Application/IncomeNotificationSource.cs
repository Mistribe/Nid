using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Income.Application;

/// <summary>
/// Notifications design §2.3: the end of a running income phase nobody continues, and the gap that follows, of the reader's own sources,
/// as signals. No amount travels: a notification says what and when, the screen has the figures.
/// </summary>
public sealed class IncomeNotificationSource(IncomeSourcesUpcoming upcoming) : INotificationSource
{
    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    public string Name => "income";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(UpcomingKinds.IncomePhaseEnd, "due_date", NotificationLevel.Action, "income", 30),
        new(UpcomingKinds.IncomeGapSoon, "due_date", NotificationLevel.Action, "income", 30),
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var events = await upcoming.UpcomingAsync(reader, VisibilityScope.Me, today, horizon, ct);
        if (!events.TryGetValue(out var list))
            return Result.Failure<IReadOnlyList<Signal>>(events.TryGetFailure(out var failure) ? failure! : new Failure("income.unknown", "The upcoming events could not be read.", null));
        // The label is the phase's: what the sentence names, never an amount.
        return Result.Success<IReadOnlyList<Signal>>(list!.Where(e => Kinds.Any(k => k.Kind == e.Kind))
            .Select(e => new Signal(SignalKeys.Dated(e.Kind, e.Item, e.On), e.Kind, [e.Item], e.On,
                e.Label is { Length: > 0 } label ? new Dictionary<string, string> { ["label"] = label } : NoParams))
            .ToList());
    }
}
