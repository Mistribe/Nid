using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;
using Npgsql;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class AccountStore(BankingDbContext db) : IAccountStore
{
    private const string EnvelopeIndex = "ix_account_envelope_item_id";

    public async Task<IReadOnlyList<BankAccount>> UpsertFromSessionAsync(Guid connection, IReadOnlyList<SessionAccount> accounts, CancellationToken ct)
    {
        var creator = await db.Connections.Where(c => c.Id == connection).Select(c => c.PersonId).SingleAsync(ct);
        var result = new List<BankAccount>(accounts.Count);
        foreach (var seen in accounts)
        {
            var iban = seen.Iban is null ? null : string.Concat(seen.Iban.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
            var ibanHash = iban is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(iban));

            var row = ibanHash is null ? null : await db.Accounts.SingleOrDefaultAsync(a => a.IbanHash == ibanHash, ct);
            if (row is null
                && await db.Sources.Where(s => s.IdentificationHash == seen.IdentificationHash).Select(s => (Guid?)s.AccountId).FirstOrDefaultAsync(ct) is { } known)
                row = await db.Accounts.SingleAsync(a => a.Id == known, ct);
            if (row is null)
            {
                row = new AccountRow
                {
                    Id = Guid.CreateVersion7(), Name = seen.Name, Kind = Wire(AccountKinds.From(seen.CashAccountType)),
                    Currency = seen.Currency.ToUpperInvariant(), CreatedByPersonId = creator,
                };
                db.Accounts.Add(row);
            }

            // An account first seen without its IBAN takes it when a session gives it.
            row.IbanHash ??= ibanHash;
            row.IbanMasked ??= iban?[^Math.Min(4, iban.Length)..];

            if (await db.Sources.FindAsync([row.Id, connection], ct) is { } source)
            {
                source.ProviderUid = seen.Uid;
                source.IdentificationHash = seen.IdentificationHash;
            }
            else
            {
                db.Sources.Add(new AccountSourceRow { AccountId = row.Id, ConnectionId = connection, ProviderUid = seen.Uid, IdentificationHash = seen.IdentificationHash });
            }

            // Saved one by one, so a later account of the same session finds this one by its IBAN or hash.
            await db.SaveChangesAsync(ct);
            result.Add(ToAccount(row));
        }

        // An account the new consent leaves out keeps the old session's uid, which fails every night: it stays, no longer read here.
        var seenIds = result.Select(a => a.Id).ToList();
        db.Sources.RemoveRange(await db.Sources.Where(s => s.ConnectionId == connection && !seenIds.Contains(s.AccountId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<BankAccount?> FindAsync(Guid id, CancellationToken ct)
        => await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } row ? ToAccount(row) : null;

    public async Task<BankAccount?> ForEnvelopeAsync(ItemId envelope, CancellationToken ct)
        => await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.EnvelopeItemId == envelope.Value, ct) is { } row ? ToAccount(row) : null;

    public async Task<IReadOnlyList<BankAccount>> ForConnectionAsync(Guid connection, CancellationToken ct)
        => (await db.Accounts.AsNoTracking()
                .Where(a => db.Sources.Any(s => s.AccountId == a.Id && s.ConnectionId == connection))
                .OrderBy(a => a.Id)
                .ToListAsync(ct))
            .Select(ToAccount).ToList();

    public async Task<IReadOnlyList<BankAccount>> AllAsync(CancellationToken ct)
        => (await db.Accounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct)).Select(ToAccount).ToList();

    public async Task<IReadOnlyList<BankAccountSource>> SourcesAsync(Guid account, CancellationToken ct)
        => (await db.Sources.AsNoTracking().Where(s => s.AccountId == account).ToListAsync(ct))
            .Select(s => new BankAccountSource(s.AccountId, s.ConnectionId, s.ProviderUid, s.IdentificationHash)).ToList();

    public async Task<Result> UpdateAsync(BankAccount account, CancellationToken ct)
    {
        // A second try when the sync wrote its columns between the read and the save (xmin): the person's columns are written again, the sync's kept.
        for (var attempt = 1; ; attempt++)
        {
            var row = await db.Accounts.SingleOrDefaultAsync(a => a.Id == account.Id, ct);
            if (row is null)
                return Result.Failure(new NotFoundFailure("bank account", account.Id.ToString()));

            if (row.EnvelopeItemId != account.Envelope?.Value)
            {
                // Another envelope starts its own history.
                row.BalanceConflictOn = null;
                row.LastValuedOn = null;
            }
            row.EnvelopeItemId = account.Envelope?.Value;
            row.CardOfAccountId = account.CardOf;
            row.AutoApplyBalance = account.AutoApplyBalance;
            row.Ignored = account.Ignored;
            row.AssociatedOn = account.AssociatedOn;
            row.AssociatedByPersonId = account.AssociatedBy?.Value;
            try
            {
                await db.SaveChangesAsync(ct);
                return Result.Success();
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                db.Entry(row).State = EntityState.Detached;
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: EnvelopeIndex })
            {
                db.Entry(row).State = EntityState.Detached;
                return Result.Failure(new Failure(BankingCodes.EnvelopeTaken, "Another bank account is associated with this envelope.", null));
            }
        }
    }

    public async Task RecordValuedAsync(Guid account, LocalDate valuedOn, LocalDate? conflictOn, CancellationToken ct)
        => await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.LastValuedOn, valuedOn)
            .SetProperty(a => a.BalanceConflictOn, conflictOn), ct);

    private static string Wire(BankAccountKind kind) => kind.ToString().ToLowerInvariant();

    private static BankAccount ToAccount(AccountRow r)
        => new(r.Id, r.IbanMasked, r.Name, Enum.Parse<BankAccountKind>(r.Kind, ignoreCase: true),
            // The callback keeps only accounts with an ISO currency: anything else here is a broken row, not a euro.
            Currency.TryCreate(r.Currency, out var currency) ? currency : throw new InvalidOperationException($"Bank account {r.Id} has no valid currency."),
            r.EnvelopeItemId is { } envelope ? new ItemId(envelope) : null, r.CardOfAccountId, r.AutoApplyBalance, r.Ignored, r.BalanceConflictOn,
            r.AssociatedOn, new PersonId(r.CreatedByPersonId), r.LastValuedOn, r.AssociatedByPersonId is { } by ? new PersonId(by) : null);
}
