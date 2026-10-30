using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>
/// Bank sync spec §5: a consent that ends soon or has ended, and a connection whose nights keep failing, as signals for the connection's
/// creator only. None carries an item id or an amount; the bank's name is the only parameter. Nothing when banking is off.
/// </summary>
public sealed class BankingNotificationSource(IConnectionStore connections, IHouseholdSettings settings, IHouseholdCalendar calendar) : INotificationSource
{
    public const string ConsentExpiring = "banking.consent_expiring";
    public const string ConsentExpired = "banking.consent_expired";
    public const string SyncFailing = "banking.sync_failing";

    public string Name => "banking";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(ConsentExpiring, NotificationFamilies.Security, NotificationLevel.Action, "banking", 14),
        new(ConsentExpired, "finding", NotificationLevel.Action, "banking", null),
        new(SyncFailing, "finding", NotificationLevel.Action, "banking", null),
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();
        if (!(await settings.ReadAsync(ct)).BankingEnabled)
            return Result.Success<IReadOnlyList<Signal>>(signals);

        // The day is the household's, like today: the same day in the key and the title.
        var zone = await calendar.ZoneAsync(ct);
        foreach (var c in await connections.MineAsync(reader, ct))
        {
            var bank = new Dictionary<string, string> { ["bank"] = c.AspspName };
            var live = c.Status is ConnectionStatus.Active or ConnectionStatus.Error;
            if (c.ValidUntil is { } until)
            {
                var on = until.InZone(zone).Date;
                if (c.Status is ConnectionStatus.Expired or ConnectionStatus.Revoked)
                    signals.Add(new Signal($"{ConsentExpired}:{c.Id:D}:{on:yyyy-MM-dd}", ConsentExpired, [], on, bank));
                else if (live)
                    foreach (var threshold in ConsentReminders.Thresholds(on, today))
                        signals.Add(new Signal($"{ConsentExpiring}:{c.Id:D}:{on:yyyy-MM-dd}:{threshold}", ConsentExpiring, [], on, bank));
            }

            // The store keeps no date for the failures: the last success (or the creation) is the same all along a streak, and a later streak follows a new success.
            if (live && c.ConsecutiveFailures >= 2)
            {
                var since = (c.LastSyncedAt ?? c.CreatedAt).InZone(zone).Date;
                signals.Add(new Signal($"{SyncFailing}:{c.Id:D}:{since:yyyy-MM-dd}", SyncFailing, [], null, bank));
            }
        }
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }
}
