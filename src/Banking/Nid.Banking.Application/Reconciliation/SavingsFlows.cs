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
public sealed class SavingsFlows(
    IAccountStore accountStore, ITransactionStore transactions, ISplitStore splits, ICategoryStore categories, IEnvelopeStore envelopes,
    IItemRegistry items, IAccessPolicy policy, IFlows flows, IHouseholdCalendar calendar, IActorAccessor actor, ILogger<SavingsFlows> log)
{
    private const int Page = 500;

    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    private sealed class Tally
    {
        public int Recorded, Superseded, Voided, Skipped;
    }

    /// <summary>Every account (null) or these: each associated with a Savings envelope, in its currency, written as BalanceValuation picks its writer.</summary>
    public async Task<SavingsFlowCounts> RunAsync(IReadOnlyCollection<Guid>? accounts, PersonId? reader, CancellationToken ct)
    {
        var list = new List<BankAccount>();
        if (accounts is null)
            list.AddRange(await accountStore.AllAsync(ct));
        else
            foreach (var id in accounts)
                if (await accountStore.FindAsync(id, ct) is { } found)
                    list.Add(found);

        var interest = await categories.ResolveKeyAsync("income.interest", ct);
        var today = await calendar.TodayAsync(ct);
        var tally = new Tally();
        foreach (var account in list)
            try
            {
                if (await AccountAsync(account, reader, interest, today, tally, ct) is { } code)
                {
                    log.LogWarning("The savings flows of bank account {Account} were refused {Code}", account.Id, code);
                    tally.Skipped++;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // The type only, as the rest of Banking: a message may carry a label or an amount.
                log.LogError("The savings flows of bank account {Account} threw {Exception}", account.Id, e.GetType().Name);
                tally.Skipped++;
            }
        return new SavingsFlowCounts(tally.Recorded, tally.Superseded, tally.Voided, tally.Skipped);
    }

    public async Task<SavingsFlowCounts> RunForLineAsync(Guid line, PersonId? reader, CancellationToken ct)
        => await transactions.FindAsync(line, ct) is { } found ? await RunAsync([found.Account], reader, ct) : new SavingsFlowCounts(0, 0, 0, 0);

    /// <summary>The failure code of a refused write, null when done or not a savings account.</summary>
    private async Task<string?> AccountAsync(BankAccount account, PersonId? reader, Guid? interest, LocalDate today, Tally tally, CancellationToken ct)
    {
        if (account is not { Envelope: { } envelope, AssociatedOn: { } associatedOn }
            || (await envelopes.FindAsync(envelope, ct))?.Kind != EnvelopeKind.Savings
            || (await items.FindAsync(envelope, ct))?.Currency != account.Currency.Code)
            return null;
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
        var read = new List<BankTransaction>();
        IReadOnlyList<BankTransaction> page;
        do
        {
            page = await transactions.ListAsync([account.Id], associatedOn.With(DateAdjusters.StartOfMonth), null, read.Count, Page, ct);
            read.AddRange(page);
        } while (page.Count == Page);

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
        var existing = current!.Where(f => f.ExternalRef?.StartsWith(SavingsFlowPlan.RefPrefix, StringComparison.Ordinal) == true)
            .ToDictionary(f => f.ExternalRef!, StringComparer.Ordinal);
        // Core refuses a flow dated after today: a line booked ahead waits for its day, the others are written.
        var wanted = SavingsFlowPlan.Plan(lines, typed, associatedOn, account.Currency.Code).Where(w => w.OccurredOn <= today).ToList();

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

        // Only a flow of a line read here: one whose line was purged, or is older than the association, stays as it is.
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
