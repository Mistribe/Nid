using System.Globalization;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>
/// Bank sync spec §5: a consent that ends soon or has ended, and a connection whose nights keep failing, as signals for the connection's
/// creator only. None carries an item id or an amount; the bank's name is the only parameter. Nothing when banking is off.
/// 5C spec §6.3, §6.6: a line that paid another amount than its charge, and a charge the bank never took, as findings for the readers of the item
/// (amounts as decimal strings, never a label).
/// </summary>
public sealed class BankingNotificationSource(
    IConnectionStore connections, IHouseholdSettings settings, IHouseholdCalendar calendar, ILinkStore links, ExpectedChargeReader charges,
    IAccessPolicy policy, IAccountStore accounts, BankingAccess access) : INotificationSource
{
    public const string ConsentExpiring = "banking.consent_expiring";
    public const string ConsentExpired = "banking.consent_expired";
    public const string SyncFailing = "banking.sync_failing";
    public const string AmountDiffers = "banking.amount_differs";
    public const string ChargeMissing = "banking.charge_missing";

    /// <summary>How far back a charge still counts as not taken.</summary>
    private const int MissingDays = 62;

    public string Name => "banking";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        new(ConsentExpiring, NotificationFamilies.Security, NotificationLevel.Action, "banking", 14),
        new(ConsentExpired, "finding", NotificationLevel.Action, "banking", null),
        new(SyncFailing, "finding", NotificationLevel.Action, "banking", null),
        new(AmountDiffers, "finding", NotificationLevel.Action, "banking", null),
        new(ChargeMissing, "finding", NotificationLevel.Action, "banking", null),
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

        await AddAmountDiffersAsync(signals, reader, today, ct);
        await AddChargeMissingAsync(signals, reader, today, zone, ct);
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }

    /// <summary>
    /// The same-currency subscription differences got a change-price proposal instead, unless the item has a reader who does not read the
    /// line's account (§0 n°1).
    /// </summary>
    private async Task AddAmountDiffersAsync(List<Signal> signals, PersonId reader, LocalDate today, CancellationToken ct)
    {
        var differing = new List<(BankLink Link, BankTransaction Line)>();
        foreach (var x in await links.OutcomeSinceAsync(LinkOutcome.PriceDiffers, today.PlusDays(-TransactionLinker.AskDays), ct))
            if (x.Line.BookedOn <= today && x.Link is { TargetItem: { } item, Expected: { } expected }
                && !(x.Link.TargetModule == "subscriptions" && expected.Currency.Code == x.Line.Currency.Code
                     && await access.ItemReadersReadAsync(x.Line.Account, item, ct)))
                differing.Add((x.Link, x.Line));
        var readable = await policy.CheckManyAsync(reader, [.. differing.Select(x => x.Link.TargetItem!.Value).Distinct()], AccessKind.Read, ct);
        // D19: the line's amount is its account's reader's to know, not every reader of the item.
        var readableAccounts = await ReadableAccountsAsync(differing.Select(x => x.Line.Account), ct);
        foreach (var (link, line) in differing.Where(x => readable.Contains(x.Link.TargetItem!.Value) && readableAccounts.Contains(x.Line.Account)))
        {
            var parameters = new Dictionary<string, string>
            {
                ["amount"] = Math.Abs(line.Amount).ToString(CultureInfo.InvariantCulture),
                ["expected"] = link.Expected!.Value.AmountText,
                ["currency"] = line.Currency.Code,
            };
            if (PageOf(link.TargetModule, link.TargetKey) is { } page)
                parameters["module"] = page;
            signals.Add(new Signal($"{AmountDiffers}:{line.Id:N}", AmountDiffers, [link.TargetItem!.Value], line.BookedOn, parameters));
        }
    }

    /// <summary>The accounts, among <paramref name="ids"/>, that the actor (the notified reader) reads.</summary>
    private async Task<HashSet<Guid>> ReadableAccountsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet();
        var known = (await accounts.AllAsync(ct)).Where(a => wanted.Contains(a.Id)).ToList();
        return [.. (await access.ReadableAsync(known, ct)).Select(r => r.Account.Id)];
    }

    /// <summary>
    /// A charge of an item the bank already paid once, on an account still synchronised, that no line has paid within its tolerance.
    /// Only the charges due after the item's first link count: the ones before predate the bank's view of it.
    /// </summary>
    private async Task AddChargeMissingAsync(List<Signal> signals, PersonId reader, LocalDate today, DateTimeZone zone, CancellationToken ct)
    {
        var due = await charges.ReadableAsync(reader, today.PlusDays(-MissingDays), today, ct);
        if (due.Count == 0)
            return;
        var all = await links.ForItemsAsync([.. due.Select(c => c.Item).Distinct()], ct);
        // D19: links on an account the reader does not read tell nothing about it, not even that it paid.
        var readableAccounts = await ReadableAccountsAsync(all.Select(x => x.Line.Account), ct);
        var linked = all.Where(x => readableAccounts.Contains(x.Line.Account)).ToLookup(x => x.Link.TargetItem!.Value);

        var active = (await connections.ActiveAsync(ct)).Select(c => c.Id).ToHashSet();
        var synchronised = new Dictionary<Guid, bool>();
        foreach (var account in linked.SelectMany(g => g).Select(x => x.Line.Account).Distinct())
            synchronised[account] = (await accounts.SourcesAsync(account, ct)).Any(s => active.Contains(s.Connection));

        foreach (var charge in due)
        {
            var mine = linked[charge.Item].ToList();
            var firstLink = mine.Where(x => synchronised[x.Line.Account]).Select(x => x.Link.DecidedAt.InZone(zone).Date).DefaultIfEmpty().Min();
            if (firstLink == default || charge.DueOn <= firstLink || charge.DueOn > today.PlusDays(-charge.ToleranceDays - 2))
                continue;
            // A line of the other direction (a property's cost) does not pay it (a rent).
            var paid = mine.Any(x => x.Link.TargetKey == charge.Key
                || (x.Line.Amount > 0 == charge.Credit
                    && x.Line.BookedOn >= charge.DueOn.PlusDays(-charge.ToleranceDays) && x.Line.BookedOn <= charge.DueOn.PlusDays(charge.ToleranceDays + 2)));
            if (!paid)
                signals.Add(new Signal($"{ChargeMissing}:{charge.Item.Value:N}:{charge.Key}", ChargeMissing, [charge.Item], charge.DueOn, new Dictionary<string, string>
                {
                    ["amount"] = charge.Amount.AmountText,
                    ["currency"] = charge.Amount.Currency.Code,
                    ["module"] = charge.Kind switch
                    {
                        ChargeKind.Subscription => "subscriptions",
                        ChargeKind.LoanInstalment => "loans",
                        ChargeKind.PropertyCost or ChargeKind.Rent => "properties",
                        _ => throw new InvalidOperationException($"No page for the charge kind {charge.Kind}."),
                    },
                }));
        }
    }

    /// <summary>The screen of the link's target, <c>null</c> when it is unknown (the notice then opens the home page): a loan's key is its due date, a property's carries a prefix.</summary>
    private static string? PageOf(string? module, string? key) => (module, key) switch
    {
        ("subscriptions", _) => "subscriptions",
        ("realestate", not null) when key.StartsWith("cost:") || key.StartsWith("rent:") || key.StartsWith("flow:") => "properties",
        ("realestate", not null) when LocalDatePattern.Iso.Parse(key).Success => "loans",
        _ => null,
    };
}
