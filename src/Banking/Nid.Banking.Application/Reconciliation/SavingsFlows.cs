using Microsoft.Extensions.Logging;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>What one pass did: flows recorded, superseded and voided, accounts skipped (a failure, or nobody who writes the envelope).</summary>
public sealed record SavingsFlowCounts(int Recorded, int Superseded, int Voided, int Skipped);

/// <summary>
/// #16: the bank lines of an account associated with a savings envelope kept as the envelope's flows (<see cref="SavingsFlowPlan"/>), as an
/// idempotent pass: what is missing is recorded, what changed superseded, what is no longer wanted voided. Only flows with a <c>bank:</c> ref
/// of a line read in this pass are ever touched. A failing account is logged, never a label or an amount, skipped, and the others go on.
/// </summary>
public sealed partial class SavingsFlows(
    IAccountStore accountStore, IConnectionStore connections, ITransactionStore transactions, ISplitStore splits, ICategoryStore categories, IEnvelopeStore envelopes,
    IItemRegistry items, IAccessPolicy policy, IFlows flows, CategoryRuleApplier categoryRules, IHouseholdCalendar calendar, IActorAccessor actor,
    ILogger<SavingsFlows> log)
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The savings flows of bank account {Account} were refused {Code}")]
    private partial void LogRefused(Guid account, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "The savings flows of bank account {Account} threw {Exception}")]
    private partial void LogThrew(Guid account, string exception);

    private const int Page = 500;

    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    private sealed class Tally
    {
        public int Recorded, Superseded, Voided, Skipped;
    }

    /// <summary>
    /// Every account (null) or these: each associated with a Savings envelope, in its currency, written as BalanceValuation picks its writer.
    /// With no <paramref name="reader"/> (the sync, a statement import, the backfill), the reader is the creator of a connection that reads
    /// the account, as the sync values it; an account read from statement files only has none, and goes to who associated it.
    /// </summary>
    public async Task<SavingsFlowCounts> RunAsync(IReadOnlyCollection<Guid>? accounts, PersonId? reader, CancellationToken ct)
    {
        var list = await SavingsAccountsAsync(accounts, ct);
        var active = reader is null && list.Count > 0 ? await connections.ActiveAsync(ct) : [];
        var interest = await categories.ResolveKeyAsync("income.interest", ct);
        var today = await calendar.TodayAsync(ct);
        var tally = new Tally();
        foreach (var account in list)
            try
            {
                if (await AccountAsync(account, reader, active, interest, today, tally, ct) is { } code)
                {
                    LogRefused(account.Id, code);
                    tally.Skipped++;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // The type only, as the rest of Banking: a message may carry a label or an amount.
                LogThrew(account.Id, e.GetType().Name);
                tally.Skipped++;
            }
        return new SavingsFlowCounts(tally.Recorded, tally.Superseded, tally.Voided, tally.Skipped);
    }

    /// <summary>
    /// The lines these savings accounts (all when null) count from take their bank hint (« Intérêts »): a line reconciled before its account
    /// went with a savings envelope was hinted as an ordinary account's. Before <see cref="RunAsync"/> on an association, and at startup.
    /// </summary>
    public async Task HintAsync(IReadOnlyCollection<Guid>? accounts, CancellationToken ct)
    {
        foreach (var account in await SavingsAccountsAsync(accounts, ct))
            await categoryRules.HintAsync(await LinesAsync(account, ct), ct);
    }

    /// <summary>
    /// The accounts (all when null) whose lines are kept as flows: associated with a Savings envelope in their own currency, still open (#505:
    /// a closed envelope takes no flow, as Investments refuses one; its flows stay as they are, and a reopened envelope catches up).
    /// </summary>
    private async Task<List<BankAccount>> SavingsAccountsAsync(IReadOnlyCollection<Guid>? accounts, CancellationToken ct)
    {
        var list = new List<BankAccount>();
        if (accounts is null)
            list.AddRange(await accountStore.AllAsync(ct));
        else
            foreach (var id in accounts)
                if (await accountStore.FindAsync(id, ct) is { } found)
                    list.Add(found);
        var kept = new List<BankAccount>();
        foreach (var account in list)
            if (account is { Envelope: { } envelope, AssociatedOn: not null }
                && (await envelopes.FindAsync(envelope, ct))?.Kind == EnvelopeKind.Savings
                && await items.FindAsync(envelope, ct) is { ArchivedAt: null } item
                && item.Currency == account.Currency.Code)
                kept.Add(account);
        return kept;
    }

    /// <summary>Every line of a savings account since the first day of its association's month.</summary>
    // ponytail: every pass rereads all these lines and every flow of the envelope; once accounts hold years of lines, read from the last pass's date.
    private async Task<List<BankTransaction>> LinesAsync(BankAccount account, CancellationToken ct)
    {
        var read = new List<BankTransaction>();
        IReadOnlyList<BankTransaction> page;
        do
        {
            page = await transactions.ListAsync([account.Id], account.AssociatedOn!.Value.With(DateAdjusters.StartOfMonth), null, read.Count, Page, ct);
            read.AddRange(page);
        } while (page.Count == Page);
        return read;
    }

    public async Task<SavingsFlowCounts> RunForLineAsync(Guid line, PersonId? reader, CancellationToken ct)
        => await transactions.FindAsync(line, ct) is { } found ? await RunAsync([found.Account], reader, ct) : new SavingsFlowCounts(0, 0, 0, 0);

    /// <summary>The failure code of a refused write, null when done.</summary>
    private async Task<string?> AccountAsync(
        BankAccount account, PersonId? reader, IReadOnlyList<BankConnection> active, Guid? interest, LocalDate today, Tally tally, CancellationToken ct)
    {
        var (envelope, associatedOn) = (account.Envelope!.Value, account.AssociatedOn!.Value);
        if (reader is null && active.Count > 0)
        {
            var sources = (await accountStore.SourcesAsync(account.Id, ct)).Select(s => s.Connection).ToHashSet();
            foreach (var creator in active.Where(c => sources.Contains(c.Id)).Select(c => c.Person))
                if ((await policy.CheckAsync(creator, envelope, AccessKind.Write, ct)).IsSuccess)
                {
                    reader = creator;
                    break;
                }
        }
        if (await BalanceValuation.WriterAsync(policy, reader, account, envelope, ct) is not { } writer)
        {
            tally.Skipped++;
            return null;
        }

        var saved = actor.Current;
        actor.Current = Actor.BankSync(writer);
        try
        {
            return await WriteAsync(account, envelope, associatedOn, interest, today, tally, ct);
        }
        finally
        {
            actor.Current = saved;
        }
    }

    private async Task<string?> WriteAsync(
        BankAccount account, ItemId envelope, LocalDate associatedOn, Guid? interest, LocalDate today, Tally tally, CancellationToken ct)
    {
        var read = await LinesAsync(account, ct);
        var parts = await splits.PartsAsync([.. read.Where(l => l.Amount > 0).Select(l => l.Id)], ct);
        var lines = read.Select(l => new SavingsLine(l.Id, l.BookedOn, l.Amount, l.Currency.Code, l.Label, InterestOf(l, parts, interest))).ToList();

        var listed = await flows.ListAsync(envelope, Everything, null, ct);
        if (listed.TryGetFailure(out var listFailure) || !listed.TryGetValue(out var current))
            return listFailure?.Code;
        var typed = current!
            .Where(f => f.ExternalRef?.StartsWith(SavingsFlowPlan.RefPrefix, StringComparison.Ordinal) != true && f.Kind is FlowKind.Contribution or FlowKind.Withdrawal)
            .OrderBy(f => f.OccurredOn).ThenBy(f => f.Id)
            .Select(f => new TypedFlow(f.Id, f.OccurredOn, f.Kind, f.Amount.Amount))
            .ToList();
        var openedOn = current!.Where(f => f.Kind == FlowKind.Opening).Max(f => (LocalDate?)f.OccurredOn);
        var existing = current!.Where(f => f.ExternalRef?.StartsWith(SavingsFlowPlan.RefPrefix, StringComparison.Ordinal) == true)
            .ToDictionary(f => f.ExternalRef!, StringComparer.Ordinal);
        // Core refuses a flow dated after today: a line booked ahead waits for its day, the others are written.
        var wanted = SavingsFlowPlan.Plan(lines, typed, associatedOn, openedOn, account.Currency.Code).Where(w => w.OccurredOn <= today).ToList();

        var missing = new List<NewFlow>();
        foreach (var w in wanted)
        {
            var money = Money.Create(w.Amount, account.Currency);
            if (money.TryGetFailure(out var moneyFailure) || !money.TryGetValue(out var amount))
                return moneyFailure?.Code;
            var flow = new NewFlow(envelope, w.OccurredOn, w.Kind, amount!, w.Category, w.Ref, w.Note, FactSource.BankApi);
            if (!existing.TryGetValue(w.Ref, out var held))
                missing.Add(flow);
            else if (held.Kind != w.Kind || held.Amount.Amount != w.Amount || held.OccurredOn != w.OccurredOn || held.Category != w.Category || held.Note != w.Note)
            {
                if ((await flows.SupersedeAsync(envelope, held.Id, flow, ct)).TryGetFailure(out var failure))
                    return failure!.Code;
                tally.Superseded++;
            }
        }

        // Only a flow of a line read here: one whose line was purged, or is older than the association, stays as it is. A line read but now
        // inside a later-typed opening is no longer wanted: its flow goes.
        var readIds = read.Select(l => l.Id).ToHashSet();
        var wantedRefs = wanted.Select(w => w.Ref).ToHashSet(StringComparer.Ordinal);
        foreach (var (reference, held) in existing)
            if (!wantedRefs.Contains(reference) && LineOf(reference) is { } line && readIds.Contains(line))
            {
                if ((await flows.VoidAsync(envelope, held.Id, ct)).TryGetFailure(out var failure))
                    return failure!.Code;
                tally.Voided++;
            }

        if (missing.Count > 0)
        {
            if ((await flows.RecordManyAsync(envelope, missing, ct)).TryGetFailure(out var failure))
                return failure!.Code;
            tally.Recorded += missing.Count;
        }
        return null;
    }

    /// <summary>A credit's interest: all of it when filed under interest, else its parts filed there, within [0, amount]; a debit has none.</summary>
    private static decimal InterestOf(BankTransaction line, IReadOnlyDictionary<Guid, IReadOnlyList<TransactionPart>> parts, Guid? interest)
    {
        if (line.Amount <= 0 || interest is not { } category)
            return 0;
        if (line.Category == category)
            return line.Amount;
        var sum = parts.GetValueOrDefault(line.Id)?.Where(p => p.Category == category).Sum(p => p.Amount) ?? 0;
        return Math.Clamp(sum, 0, line.Amount);
    }

    /// <summary>The line id of <c>bank:&lt;id&gt;</c> or <c>bank:&lt;id&gt;:interest</c>.</summary>
    private static Guid? LineOf(string reference)
        => Guid.TryParse(reference.AsSpan(SavingsFlowPlan.RefPrefix.Length, Math.Min(36, reference.Length - SavingsFlowPlan.RefPrefix.Length)), out var id) ? id : null;
}
