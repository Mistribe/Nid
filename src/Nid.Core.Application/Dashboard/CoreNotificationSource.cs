using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Application.Dashboard;

/// <summary>
/// Notifications design §2.3: what the dashboard says about the household as a whole, as signals: a currency without a rate, and
/// the month's routine when a value is to update. Neither is about one item, so neither carries an item id or an amount.
/// </summary>
public sealed class CoreNotificationSource(DashboardReader dashboard, IPersonalAccessTokens tokens, IClock clock) : INotificationSource
{
    public const string FxRateMissing = "fx_rate_missing";
    public const string RoutineDue = "routine_due";
    public const string TokenExpiring = "token_expiring";

    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    public string Name => "core";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(FxRateMissing, "finding", NotificationLevel.Info, "core", null),
        new(RoutineDue, "finding", NotificationLevel.Action, "core", null),
        new(TokenExpiring, NotificationFamilies.Security, NotificationLevel.Action, "core", 7),
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var read = await dashboard.ReadAsync(reader, VisibilityScope.Me, today, ct);
        if (!read.TryGetValue(out var view))
            return Result.Failure<IReadOnlyList<Signal>>(read.TryGetFailure(out var failure) ? failure! : new Failure("core.unknown", "The dashboard could not be read.", null));

        var signals = new List<Signal>();
        if (view!.PeopleMissingCurrency is { Length: > 0 } currency)
            signals.Add(new Signal(SignalKeys.Household(FxRateMissing, currency), FxRateMissing, [], null, new Dictionary<string, string> { ["currency"] = currency }));
        // The routine is the web's: only the values to update count (web/src/features/dashboard/todo.ts), not the income gap.
        if (view.Todo.Any(t => t.Kind is "stale_value" or "no_value"))
            signals.Add(new Signal(SignalKeys.Household(RoutineDue, $"{today:yyyy-MM}"), RoutineDue, [], null, NoParams));

        // The expiry day is read in UTC (ponytail: up to a day off the household's clock, the same day in the key and the title).
        var now = clock.GetCurrentInstant();
        foreach (var t in await tokens.ListAsync(reader, ct))
        {
            var on = t.ExpiresAt.InUtc().Date;
            if (t.RevokedAt is null && t.ExpiresAt > now && on <= horizon)
                signals.Add(new Signal($"{TokenExpiring}:{t.Id:D}:{on:yyyy-MM-dd}", TokenExpiring, [], on, NoParams));
        }
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }
}
