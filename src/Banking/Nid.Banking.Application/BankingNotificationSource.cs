using System.Globalization;
using Microsoft.Extensions.Logging;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Application.Spending;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using Nid.Income.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>
/// Bank sync spec §5: a consent that ends soon or has ended, and a connection whose nights keep failing, as signals for the connection's
/// creator only. None carries an item id or an amount; the bank's name is the only parameter. Nothing when banking is off.
/// 5C spec §6.3, §6.6: a line that paid another amount than its charge, and a charge the bank never took, as findings for the readers of the item
/// (amounts as decimal strings, never a label). Issue #81: the shortfall ahead on a checking account and idle cash on the checking accounts,
/// opt-in, whose figures only the centre shows. Issue #362: a card under its minimum usage a few days before the month ends, opt-in,
/// for whoever sets the card. Issue #305: a category whose month runs away, opt-in, over « Foyer »'s lines the reader reads.
/// </summary>
public sealed partial class BankingNotificationSource(
    IConnectionStore connections, IHouseholdSettings settings, IHouseholdCalendar calendar, ILinkStore links, ExpectedChargeReader charges,
    IAccessPolicy policy, IAccountStore accounts, BankingAccess access, IBalanceStore balances, IExpectedIncome income, ISignalSettings signalSettings,
    IEmergencyBuffer buffers, ISavingsRoom savings, ITransactionStore transactions, IAccountReadLog reads, IClock clock,
    GetSpendingHandler spending, SpendingAccounts scopes, ILogger<BankingNotificationSource> logger) : INotificationSource
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The spending of {Person} could not be read: {Code}")]
    private partial void LogSpendingUnread(Guid person, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The emergency buffer of {Person} could not be read: {Code}")]
    private partial void LogBufferUnread(Guid person, string code);

    public const string ConsentExpiring = "banking.consent_expiring";
    public const string ConsentExpired = "banking.consent_expired";
    public const string SyncFailing = "banking.sync_failing";
    /// <summary>#414: a connection the transfer of the installation application ended, while its creator has no application and live connection of their own.</summary>
    public const string ApplicationNeeded = "banking.application_needed";
    public const string AmountDiffers = "banking.amount_differs";
    public const string ChargeMissing = "banking.charge_missing";
    /// <summary>Not prefixed: the settings screen keys its copy by kind (<c>notification_settings.signal.&lt;kind&gt;</c>).</summary>
    public const string ShortfallAhead = "shortfall_ahead";
    public const string IdleCash = "idle_cash";
    public const string CardUnused = "card_unused";
    public const string UnusualSpending = "unusual_spending";

    /// <summary>How far back a charge still counts as not taken.</summary>
    private const int MissingDays = 62;

    public string Name => "banking";

    public IReadOnlyList<NotificationKind> Kinds { get; } =
    [
        // A connection is its creator's business: never for the household.
        new(ConsentExpiring, NotificationFamilies.Security, NotificationLevel.Action, "banking", 14) { Personal = true, Template = BankingNotificationCopy.ConsentExpiring },
        new(ConsentExpired, "finding", NotificationLevel.Action, "banking", null) { Personal = true, Template = BankingNotificationCopy.ConsentExpired },
        new(SyncFailing, "finding", NotificationLevel.Action, "banking", null) { Personal = true, Template = BankingNotificationCopy.SyncFailing },
        new(ApplicationNeeded, "finding", NotificationLevel.Action, "banking", null) { Personal = true, Template = BankingNotificationCopy.ApplicationNeeded },
        new(AmountDiffers, "finding", NotificationLevel.Action, "banking", null) { Template = BankingNotificationCopy.AmountDiffers },
        new(ChargeMissing, "finding", NotificationLevel.Action, "banking", null) { Template = BankingNotificationCopy.ChargeMissing },
        // Issue #81: the margin, in the account's currency.
        new(ShortfallAhead, "finding", NotificationLevel.Action, "banking", null, new SignalThreshold(0m, 0m, 1_000_000m, "eur"))
        {
            Template = BankingNotificationCopy.ShortfallAhead,
        },
        // The buffer in months of commitments, when no emergency fund says it.
        new(IdleCash, "finding", NotificationLevel.Info, "banking", null, new SignalThreshold(3m, 1m, 24m, "months")) { Template = BankingNotificationCopy.IdleCash },
        // Issue #362: how many days before the month's end.
        new(CardUnused, "finding", NotificationLevel.Action, "banking", null, new SignalThreshold(5m, 1m, 20m, "days")) { Template = BankingNotificationCopy.CardUnused },
        // Issue #305: how far above the usual month, in percent.
        new(UnusualSpending, "finding", NotificationLevel.Info, "banking", null, new SignalThreshold(50m, 10m, 500m, "pct"))
        {
            Template = BankingNotificationCopy.UnusualSpending,
        },
    ];

    public async Task<Result<IReadOnlyList<Signal>>> SignalsAsync(PersonId reader, LocalDate today, LocalDate horizon, CancellationToken ct)
    {
        var signals = new List<Signal>();
        if (!(await settings.ReadAsync(ct)).BankingEnabled)
            return Result.Success<IReadOnlyList<Signal>>(signals);

        // The day is the household's, like today: the same day in the key and the title.
        var zone = await calendar.ZoneAsync(ct);
        var mine = await connections.MineAsync(reader, ct);
        foreach (var c in mine)
        {
            var bank = new Dictionary<string, string> { ["bank"] = c.AspspName };
            var live = c.Status is ConnectionStatus.Active or ConnectionStatus.Error;
            if (c.LastErrorCode == BankingCodes.ApplicationTransferred)
                continue; // told once below, not as an expired consent
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

        // Derived from state: gone once the reader has a readable application of their own and a bank linked through it.
        if (mine.FirstOrDefault(c => c.LastErrorCode == BankingCodes.ApplicationTransferred) is { } ended
            && !(mine.Any(c => c.Status is ConnectionStatus.Active or ConnectionStatus.Error) && await access.ConfiguredAsync(reader, ct)))
            signals.Add(new Signal($"{ApplicationNeeded}:{ended.Id:D}", ApplicationNeeded, [], null, new Dictionary<string, string>()));

        await AddAmountDiffersAsync(signals, reader, today, ct);
        await AddChargeMissingAsync(signals, reader, today, zone, ct);
        var turnedOn = await signalSettings.ForAsync(reader, ct);
        if (turnedOn.TryGetValue(ShortfallAhead, out var margin))
            await AddShortfallAsync(signals, reader, today, margin, ct);
        if (turnedOn.TryGetValue(IdleCash, out var months) && await IdleCashAsync(reader, today, months, ct) is { } idle)
            signals.Add(idle);
        if (turnedOn.TryGetValue(CardUnused, out var days))
            await AddCardUnusedAsync(signals, reader, today, (int)days, ct);
        if (turnedOn.TryGetValue(UnusualSpending, out var percent))
            await AddUnusualSpendingAsync(signals, reader, today, percent, ct);
        return Result.Success<IReadOnlyList<Signal>>(signals);
    }

    /// <summary>
    /// Issue #362: each card the reader sets whose minimum usage is not reached in the month's last <paramref name="days"/> days, once
    /// per card and month (the key). Never on stale data: a card no live connection reads, not read for <see cref="CardUsage.Fresh"/>, or
    /// with no line of its own at all (a bank that posts card payments on the checking account), says nothing; its connection's
    /// alerts say why. The card is named by its display name and masked number only.
    /// </summary>
    private async Task AddCardUnusedAsync(List<Signal> signals, PersonId reader, LocalDate today, int days, CancellationToken ct)
    {
        if (!CardUsage.Due(today, days))
            return;
        var monthStart = today.With(DateAdjusters.StartOfMonth);
        var active = (await connections.ActiveAsync(ct)).Select(c => c.Id).ToHashSet();
        var freshSince = clock.GetCurrentInstant() - CardUsage.Fresh;
        foreach (var (card, canWrite) in await access.ReadableAsync(await accounts.AllAsync(ct), ct))
        {
            if (!canWrite || card.Kind != BankAccountKind.Card || card.MinPayments is not { } min || card.Ignored)
                continue;
            if (!(await accounts.SourcesAsync(card.Id, ct)).Any(s => active.Contains(s.Connection))
                || !(await reads.SinceAsync([card.Id], freshSince, ct)).Any(r => !r.RateLimited)
                || await transactions.LatestBookedOnAsync(card.Id, null, ct) is null)
                continue;
            var lines = await transactions.ListAsync([card.Id], monthStart, today, 0, int.MaxValue, ct);
            var count = CardUsage.Payments(lines.Select(l => (l.BookedOn, l.Amount, l.NormalizedLabel)), monthStart, today, card.CountWithdrawals);
            if (count >= min)
                continue;
            signals.Add(new Signal($"{CardUnused}:{card.Id:D}:{today:yyyy-MM}", CardUnused, [], today.With(DateAdjusters.EndOfMonth),
                new Dictionary<string, string>
                {
                    ["card"] = card.Name, ["number"] = card.IbanMasked ?? "",
                    ["count"] = count.ToString(CultureInfo.InvariantCulture), ["min"] = min.ToString(CultureInfo.InvariantCulture),
                }));
        }
    }

    /// <summary>
    /// Issue #305: each expense category of « Foyer » (the accounts every adult reads, as the spending view counts them for the reader) whose
    /// month runs away (<see cref="Domain.UnusualSpending"/>), once per category and month (the key). A child that stands out above its
    /// usual month says it for its parent. The centre alone shows the figures and the three heaviest lines (bank text, which may hold amounts).
    /// </summary>
    private async Task AddUnusualSpendingAsync(List<Signal> signals, PersonId reader, LocalDate today, decimal percent, CancellationToken ct)
    {
        var month = today.ToYearMonth();
        var read = await spending.HandleAsync(new GetSpendingQuery(VisibilityScope.Household, month, null, null), ct);
        if (!read.TryGetValue(out var view))
        {
            LogSpendingUnread(reader.Value, read.TryGetFailure(out var f) ? f!.Code : "banking.unknown");
            return;
        }
        static decimal? Amount(string? text) => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);
        IReadOnlyList<Guid>? accounts = null;

        async Task<string?> TellAsync(SpendingCategoryView category, bool childTold)
        {
            var (spent, usual, planned, expected) = (Amount(category.Amount)!.Value, Amount(category.Average), Amount(category.Budget?.Planned),
                Amount(category.Budget?.ExpectedSoFar));
            var reason = Domain.UnusualSpending.Find(today, percent, spent, usual, planned, expected);
            if (reason is null || (childTold && reason == Domain.UnusualSpending.AboveUsual))
                return reason;
            accounts ??= await scopes.ForAsync(VisibilityScope.Household, ct);
            var (lines, _) = await transactions.SpendingLinesAsync(accounts, [category.Id, .. category.Children.Select(c => c.Id)], false,
                month.ToDateInterval().Start, month.ToDateInterval().End, 0, int.MaxValue, ct);
            var figures = new Dictionary<string, string>
            {
                ["category"] = category.Id.ToString("D"), ["name"] = category.Name, ["month"] = YearMonthPattern.Iso.Format(month), ["reason"] = reason,
                ["spent_amount"] = category.Amount, ["currency"] = view!.Currency,
                // ponytail: ranked by the lines' own amounts, whatever their currency; most households hold one.
                ["lines"] = string.Join(", ", lines.Where(l => l.Amount < 0).OrderBy(l => l.Amount).Take(3).Select(l => l.Line.Label.Trim())),
            };
            if (category.Average is { } average)
                figures["usual_amount"] = average;
            if (category.Budget is { } budget)
                figures["budget_amount"] = budget.Planned;
            if (reason == Domain.UnusualSpending.BudgetPace)
                figures["projected_amount"] = decimal.Round(Domain.UnusualSpending.Projected(planned!.Value, expected!.Value, spent, today), 2)
                    .ToString(CultureInfo.InvariantCulture);
            signals.Add(new Signal(SignalKeys.Monthly(UnusualSpending, new ItemId(category.Id), today), UnusualSpending, [], null, figures));
            return reason;
        }

        foreach (var top in view!.Categories)
        {
            var childTold = false;
            foreach (var child in top.Children)
                childTold |= await TellAsync(child, false) is not null;
            await TellAsync(top, childTold);
        }
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
                    ["module"] = charge.Kind.Page,
                }));
        }
    }

    /// <summary>
    /// Issue #81: the synced checking accounts the reader reads, each balance less the debits due before the next income. A charge counts on
    /// the account its item's last debit line was on, and when none was ever matched, on the reader's only synced checking account. A charge
    /// already linked to a line is in the balance; a credit (a rent) is not counted on: it may come late.
    /// </summary>
    private async Task AddShortfallAsync(List<Signal> signals, PersonId reader, LocalDate today, decimal margin, CancellationToken ct)
    {
        var checking = new Dictionary<Guid, (BankAccount Account, BankBalance? Latest)>();
        foreach (var account in await SyncedCheckingAsync(ct))
            checking[account.Id] = (account, await balances.LatestAsync(account.Id, ct));
        if (!checking.Values.Any(c => Shortfalls.Fresh(c.Latest, today)))
            return;

        var end = Shortfalls.End(today, await income.NextAsync(reader, today, ct));
        var due = (await charges.ReadableAsync(reader, today, end.PlusDays(-1), ct)).Where(c => !c.Credit).ToList();
        if (due.Count == 0)
            return;
        // Every account's lines, read or not: a charge paid from the other adult's account counts on none of the reader's.
        var all = await links.ForItemsAsync([.. due.Select(c => c.Item).Distinct()], ct);
        var paidFrom = all.Where(x => x.Line.Amount < 0).GroupBy(x => x.Link.TargetItem!.Value)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Line.BookedOn).Line.Account);
        var taken = all.Select(x => $"{x.Link.TargetItem!.Value.Value:N}:{x.Link.TargetKey}").ToHashSet();
        var convert = await charges.ConverterAsync(due, today, today, ct);

        var dues = new List<Shortfalls.Due>();
        foreach (var charge in due.Where(c => !taken.Contains($"{c.Item.Value:N}:{c.Key}")))
        {
            Guid? on = paidFrom.TryGetValue(charge.Item, out var paid) ? paid : null;
            var account = on is { } id ? (checking.TryGetValue(id, out var c) ? c.Account : null) : checking.Count == 1 ? checking.Values.Single().Account : null;
            // ponytail: a charge without a rate to its account's currency is left out; most households hold one currency.
            if (account is not null && convert(charge, today, account.Currency.Code) is { } amount)
                dues.Add(new Shortfalls.Due(charge.DueOn, amount, charge.ItemName, on));
        }

        foreach (var s in Shortfalls.Find(today, end, margin, checking.ToDictionary(c => c.Key, c => c.Value.Latest), dues))
        {
            var account = checking[s.Account].Account;
            var figures = new Dictionary<string, string>
            {
                ["balance_amount"] = s.Balance.ToString(CultureInfo.InvariantCulture),
                ["charges_amount"] = s.Charges.ToString(CultureInfo.InvariantCulture),
                ["currency"] = account.Currency.Code,
                ["short_on"] = s.On.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["charges"] = string.Join(", ", s.Names),
                // Names the account, which may have no item to be named by (two checking accounts must be told apart).
                ["account"] = account.Name,
            };
            // The account's page is its envelope's « Opérations » tab; an account without one is seen in the banks' settings.
            if (account.Envelope is not null)
                figures["module"] = "investments";
            signals.Add(new Signal(SignalKeys.Dated(ShortfallAhead, new ItemId(account.Id), s.On), ShortfallAhead,
                account.Envelope is { } envelope ? [envelope] : [], s.On, figures));
        }
    }

    /// <summary>Issue #81: the checking accounts the actor (the notified reader) reads, not ignored, with a source on an active connection.</summary>
    private async Task<List<BankAccount>> SyncedCheckingAsync(CancellationToken ct)
    {
        var active = (await connections.ActiveAsync(ct)).Select(c => c.Id).ToHashSet();
        var synced = new List<BankAccount>();
        foreach (var (account, _) in await access.ReadableAsync([.. (await accounts.AllAsync(ct)).Where(a => a.Kind == BankAccountKind.Checking && !a.Ignored)], ct))
            if ((await accounts.SourcesAsync(account.Id, ct)).Any(s => active.Contains(s.Connection)))
                synced.Add(account);
        return synced;
    }

    /// <summary>
    /// Issue #81: the synced checking accounts' total stayed above the buffer every day of the last 28, while a livret the reader reads has
    /// room or an open goal is behind; once a month. No item: it is about the accounts as a whole. A failed goals read is logged, none raised.
    /// </summary>
    private async Task<Signal?> IdleCashAsync(PersonId reader, LocalDate today, decimal months, CancellationToken ct)
    {
        var reference = await settings.ReferenceCurrencyAsync(ct);
        // ponytail: an account in another currency than the reference is left out (a lower total, never a false signal); convert it if households hold several.
        var held = new List<IReadOnlyList<BankBalance>>();
        foreach (var account in (await SyncedCheckingAsync(ct)).Where(a => a.Currency == reference))
            held.Add(await balances.SinceAsync(account.Id, today.PlusDays(1 - Domain.IdleCash.Days), ct));
        if (Domain.IdleCash.LowestTotal(today, held) is not { } lowest)
            return null;

        var read = await buffers.ReadAsync(reader, (int)Math.Round(months, MidpointRounding.AwayFromZero), ct);
        if (!read.TryGetValue(out var buffer))
        {
            LogBufferUnread(reader.Value, read.TryGetFailure(out var f) ? f!.Code : "goals.unknown");
            return null;
        }
        if (buffer!.Amount is not { } amount)
            return null;
        var room = await savings.RoomAsync(reader, today, ct);
        if (Domain.IdleCash.Find(lowest, amount, room, buffer.GoalBehind) is not var (above, reason))
            return null;

        var figures = new Dictionary<string, string>
        {
            ["above_amount"] = decimal.Round(above, 2).ToString(CultureInfo.InvariantCulture),
            ["buffer_amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["currency"] = reference.Code,
            ["reason"] = reason,
        };
        if (room > 0m)
            figures["room_amount"] = decimal.Round(room, 2).ToString(CultureInfo.InvariantCulture);
        return new Signal(SignalKeys.Household(IdleCash, $"{today:yyyy-MM}"), IdleCash, [], null, figures);
    }

    /// <summary>The screen of the link's target, <c>null</c> when it is unknown (the notice then opens the home page): a loan's key is its due date, a property's carries a prefix.</summary>
    private static string? PageOf(string? module, string? key) => (module, key) switch
    {
        ("subscriptions", _) => "subscriptions",
        ("childcare", _) => "childcare",
        ("realestate", not null) when key.StartsWith("cost:") || key.StartsWith("rent:") || key.StartsWith("flow:") => "properties",
        ("realestate", not null) when LocalDatePattern.Iso.Parse(key).Success => "loans",
        _ => null,
    };
}
