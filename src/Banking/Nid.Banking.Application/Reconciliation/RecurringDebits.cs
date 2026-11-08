using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// 5C spec §6.4, nightly: the unlinked debits of the last 13 months that come back at a steady rhythm and pay no known charge become a
/// <c>banking.create_subscription</c> proposal, once per (account, stem) ever, deposited by a reader of the account (D22). A failure is
/// logged with its code, never a label or an amount, and never fails the job.
/// </summary>
public sealed class RecurringDebits(
    BankingAccess access, ITransactionStore transactions, IAccountStore accounts, IRuleStore rules, ExpectedChargeReader charges,
    IItemRegistry items, IProposalSubmitter proposals, IActorAccessor actor, IClock clock, ILogger<RecurringDebits> log)
{
    private const int Months = 13;

    /// <summary>The widest tolerance of a charge: the charges read around a group.</summary>
    private const int ChargeMarginDays = 10;

    /// <summary>How many groups were proposed.</summary>
    public async Task<int> ProposeAsync(LocalDate today, CancellationToken ct)
    {
        try
        {
            return (await access.EnabledAsync(ct)).IsSuccess ? await ProposeAllAsync(today, ct) : 0;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError("Proposing the recurring debits threw {Exception}", e.GetType().Name);
            return 0;
        }
    }

    private async Task<int> ProposeAllAsync(LocalDate today, CancellationToken ct)
    {
        var debits = (await transactions.UnlinkedBetweenAsync(today.PlusMonths(-Months), today, ct)).Where(l => l.Amount < 0);
        var known = (await accounts.AllAsync(ct)).ToDictionary(a => a.Id);
        var proposed = 0;
        foreach (var group in Recurrence.Find([.. debits.Select(TransactionLinker.LineOf)], today))
        {
            if (!known.TryGetValue(group.Account, out var account))
                continue;
            var readers = await access.ReadersAsync(account, ct); // the owner first, when they read it
            if (readers.Count == 0 || await PaysAChargeAsync(group, readers, ct)
                || !await rules.TryRememberGroupAsync(group.Account, group.Stem, clock.GetCurrentInstant(), ct))
                continue;
            if (await SubmitAsync(group, account, known, readers, ct))
                proposed++;
        }
        return proposed;
    }

    /// <summary>Whether a line of the group is, at the cent, a charge one of the account's readers reads: then it is no unknown debit.</summary>
    private async Task<bool> PaysAChargeAsync(RecurringGroup group, IReadOnlyList<PersonId> readers, CancellationToken ct)
    {
        var (first, last) = (group.Lines[0].BookedOn, group.Lines[^1].BookedOn);
        var read = new List<ExpectedCharge>();
        foreach (var reader in readers)
            read.AddRange(await charges.ReadableAsync(reader, first.PlusDays(-ChargeMarginDays), last.PlusDays(ChargeMarginDays), ct));
        var convert = await charges.ConverterAsync(read, first, last, ct);
        var dues = read.Select(TransactionLinker.DueOf).ToList();
        return group.Lines.Any(line => ChargeMatch.Near(line, dues)
            .Any(i => ChargeMatch.Agrees(line, dues[i], convert(read[i], line.BookedOn, line.Currency)) == true));
    }

    /// <summary>
    /// The subscription as the account's envelope (a card's: its checking account's) is held, or private to the account's owner; one phase
    /// from the first line at the latest line's price. Deposited by the first reader among its owners (D22), who may create it.
    /// </summary>
    private async Task<bool> SubmitAsync(
        RecurringGroup group, BankAccount account, IReadOnlyDictionary<Guid, BankAccount> known, IReadOnlyList<PersonId> readers, CancellationToken ct)
    {
        var ruling = account.CardOf is { } parent && known.TryGetValue(parent, out var checking) ? checking : account;
        var envelope = ruling.Envelope is { } id ? await items.FindAsync(id, ct) : null;
        var shared = envelope?.Visibility == Visibility.Shared;
        IReadOnlyList<(PersonId Person, int ShareBp)> owners = envelope is null
            ? [(ruling.CreatedBy, 10_000)]
            : [.. envelope.Owners.Select(o => (o.PersonId, o.ShareBp))];
        // Only an owner creates an item (AccessRules.CanCreate); an item of dependents only, any adult: then the first reader.
        var depositor = readers.FirstOrDefault(r => owners.Any(o => o.Person == r), readers[0]);
        var latest = group.Lines[^1];
        var (count, unit) = group.Cadence switch
        {
            Cadence.Monthly => (1, "month"),
            Cadence.Quarterly => (3, "month"),
            _ => (1, "year"),
        };
        var payload = TransactionLinker.Element(new JsonObject
        {
            ["accountId"] = account.Id.ToString("D"),
            ["stem"] = group.Stem,
            ["lines"] = new JsonArray([.. group.Lines.Select(l => (JsonNode)l.Id.ToString("D"))]),
            ["subscription"] = new JsonObject
            {
                ["displayName"] = group.Stem,
                ["visibility"] = shared ? "shared" : "private",
                ["owners"] = new JsonArray([.. owners.Select(o => (JsonNode)new JsonObject { ["personId"] = o.Person.Value.ToString("D"), ["shareBp"] = o.ShareBp })]),
                ["autoRenew"] = true,
                ["note"] = $"Compte payeur : {SubscriptionProposal.Label(account)}",
                ["phases"] = new JsonArray(new JsonObject
                {
                    ["startsOn"] = TransactionLinker.Day(group.Lines[0].BookedOn),
                    ["price"] = new JsonObject { ["amount"] = TransactionLinker.Amount(Math.Abs(latest.Amount)), ["currency"] = latest.Currency },
                    ["every"] = new JsonObject { ["count"] = count, ["unit"] = unit },
                    ["payer"] = shared
                        ? new JsonObject { ["type"] = "household" }
                        : new JsonObject { ["type"] = "person", ["personId"] = owners[0].Person.Value.ToString("D") },
                }),
            },
        });

        var saved = actor.Current;
        actor.Current = Actor.BankSync(depositor);
        try
        {
            var submitted = await proposals.SubmitAsync(SubscriptionProposal.KindName, null, payload, BankSyncCollector.SourceLabel, ct);
            if (submitted.TryGetValue(out var created))
                return created;
            // The group stays remembered: a refused proposal is not retried every night.
            log.LogWarning("A recurring debit proposal was refused {Code}", submitted.TryGetFailure(out var failure) ? failure!.Code : null);
            return false;
        }
        finally
        {
            actor.Current = saved;
        }
    }
}
