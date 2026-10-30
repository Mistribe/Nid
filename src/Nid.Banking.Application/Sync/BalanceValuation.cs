using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Banking.Application.Sync;

/// <summary>What one valuation pass did, added to a sync's run counts.</summary>
public sealed record ValuationOutcome(int Proposals, int Written, int Skipped);

/// <summary>Spec §4: an associated account's stored balances becoming its envelope's values, for the sync and a statement import alike.</summary>
public sealed class BalanceValuation
{
    private sealed class Tally
    {
        public int Proposals, Written, Skipped;
    }

    /// <summary>
    /// Spec §4: each due date proposed, or written when the account applies its balances; a value a person entered or imported on that date
    /// is left alone and its date kept on the account. <c>last_valued_on</c> moves past what was done, never past a failure. Written as the
    /// reading connection's creator when they write the envelope, else as the person who associated the account (ruling R3); neither: skipped,
    /// nothing recorded as done.
    /// </summary>
    public async Task<ValuationOutcome> ValueAsync(
        IServiceProvider services, PersonId reader, BankAccount account, LocalDate today, bool manual, CancellationToken ct)
    {
        var tally = new Tally();
        await ValueAsync(services, reader, account, today, manual, tally, ct);
        return new ValuationOutcome(tally.Proposals, tally.Written, tally.Skipped);
    }

    private static async Task ValueAsync(
        IServiceProvider services, PersonId reader, BankAccount account, LocalDate today, bool manual, Tally progress, CancellationToken ct)
    {
        var envelope = account.Envelope!.Value;
        // A registry read: the currency only, whoever reads the envelope.
        if ((await services.GetRequiredService<IItemRegistry>().FindAsync(envelope, ct))?.Currency != account.Currency.Code)
        {
            progress.Skipped++;
            return;
        }

        var policy = services.GetRequiredService<IAccessPolicy>();
        PersonId? writer = null;
        foreach (var candidate in new[] { reader, account.AssociatedBy }.OfType<PersonId>())
            if ((await policy.CheckAsync(candidate, envelope, AccessKind.Write, ct)).IsSuccess)
            {
                writer = candidate;
                break;
            }
        if (writer is not { } person)
        {
            progress.Skipped++;
            return;
        }
        // A click values today in the clicker's name only: the reader of a manual run is who clicked.
        manual &= person == reader;

        var accessor = services.GetRequiredService<IActorAccessor>();
        var saved = accessor.Current;
        accessor.Current = Actor.BankSync(person);
        try
        {
            await ValueAsAsync(services, account, envelope, today, manual, progress, ct);
        }
        finally
        {
            accessor.Current = saved; // the connection's own writes stay its creator's
        }
    }

    private static async Task ValueAsAsync(
        IServiceProvider services, BankAccount account, ItemId envelope, LocalDate today, bool manual, Tally progress, CancellationToken ct)
    {
        var balances = services.GetRequiredService<IBalanceStore>();
        var associatedOn = account.AssociatedOn ?? today;
        var stored = new List<BankBalance>();
        for (var month = (account.LastValuedOn is { } done && done > associatedOn ? done : associatedOn).With(DateAdjusters.EndOfMonth);
             month <= today.With(DateAdjusters.EndOfMonth);
             month = month.PlusDays(1).With(DateAdjusters.EndOfMonth))
            stored.AddRange(await balances.InMonthAsync(account.Id, month, ct));

        var valuations = services.GetRequiredService<IValuations>();
        var proposals = services.GetRequiredService<IProposalSubmitter>();
        LocalDate? valuedOn = null, conflictOn = null;
        foreach (var (on, balanceOn) in ValuationDates.Due(associatedOn, account.LastValuedOn, stored.Select(b => b.AsOf).ToList(), today, manual))
        {
            var balance = stored.Single(b => b.AsOf == balanceOn);
            if (!(await valuations.LatestAsync(envelope, on, ct)).TryGetValue(out var latest) || !Money.Create(balance.Amount, account.Currency).TryGetValue(out var amount))
            {
                progress.Skipped++;
                break;
            }
            if (latest!.Value is { } current && current.AsOf == on && current.Source != FactSource.BankApi)
            {
                progress.Skipped++;
                valuedOn = conflictOn = on;
                continue;
            }

            var note = "Solde bancaire du " + balanceOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                + (BalancePick.IsFallback(balance.BalanceType) ? " (solde disponible)" : "");
            if (account.AutoApplyBalance)
            {
                if (!(await valuations.RecordAsync(new NewValuation(envelope, on, amount, note, FactSource.BankApi), ct)).IsSuccess)
                {
                    progress.Skipped++;
                    break;
                }
                progress.Written++;
            }
            else
            {
                var submitted = await proposals.SubmitAsync(BankSyncCollector.ProposalKind, envelope, Payload(on, amount, note), BankSyncCollector.SourceLabel, ct);
                if (!submitted.TryGetValue(out var created))
                {
                    progress.Skipped++;
                    break;
                }
                if (created)
                    progress.Proposals++;
            }
            valuedOn = on;
            conflictOn = null; // a later date valued: the warning goes
        }

        if (valuedOn is { } last)
            await services.GetRequiredService<IAccountStore>().RecordValuedAsync(account.Id, last, conflictOn, ct);
    }

    /// <summary><c>investments.record_valuation</c>'s payload: <c>{ asOf, amount { amount, currency }, note }</c>.</summary>
    private static JsonElement Payload(LocalDate on, Money amount, string note)
    {
        var body = new JsonObject
        {
            ["asOf"] = on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["amount"] = new JsonObject { ["amount"] = amount.AmountText, ["currency"] = amount.Currency.Code },
            ["note"] = note,
        };
        using var document = JsonDocument.Parse(body.ToJsonString());
        return document.RootElement.Clone();
    }
}
