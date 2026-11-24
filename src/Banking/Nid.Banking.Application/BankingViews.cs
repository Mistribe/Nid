using System.Globalization;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Application;

/// <summary>
/// Whether bank sync is on, and whether the administrator gave it an application id and the server a private key. For an administrator
/// only (issue #343), <paramref name="AppIdSaved"/> and <paramref name="KeyStatus"/> (<see cref="BankingKeyStatus"/>) say which is missing.
/// </summary>
public sealed record BankingStatusView(bool Enabled, bool Configured, bool? AppIdSaved = null, string? KeyStatus = null, string? RedirectUrl = null);

/// <summary>
/// Issue #343, Administration › Banque: the key's <see cref="BankingKeyStatus"/> (never the key), whether an application id is saved, and
/// the redirect URL Nid sends the banks to, which the application must declare. Issue #344: the last run, the next nightly run
/// (<c>null</c> when this instance schedules no job) and whether any adult has a bank connection the sync reads.
/// </summary>
/// <param name="Reads">#365: the installation's busiest account and latest refusal, counts only (no bank, no account named).</param>
public sealed record BankingDiagnosticsView(
    string KeyStatus, bool AppIdSaved, string RedirectUrl, BankSyncRunView? LastRun = null, Instant? NextRunAt = null, bool BankLinked = false,
    BankReadsView? Reads = null, IReadOnlyList<OwnApplicationView>? OwnApplications = null,
    IReadOnlyList<TransferCandidateView>? TransferCandidates = null);

/// <summary>
/// #414: a signed-up adult the installation application can be handed to, with the number of their connections made with it; listed
/// only while that application is configured, those with connections first.
/// </summary>
public sealed record TransferCandidateView(Guid PersonId, string Name, int Connections);

/// <summary>#382: an adult who declared their own Enable Banking application; neither its id nor its key.</summary>
public sealed record OwnApplicationView(Guid PersonId, string Name);

/// <summary>#382: the actor's own application: <paramref name="KeyStatus"/> is <c>loaded</c> or <c>unreadable</c> (the key ring lost it), never the key.</summary>
public sealed record MyApplicationView(string ApplicationId, string KeyStatus, Instant KeyLoadedAt, bool InUse);

/// <summary>
/// #365: Nid's own count of an account's reads at its bank, an estimate (<see cref="BankReads"/>): <paramref name="Unattended"/> of
/// <paramref name="Limit"/> over the last 24 hours, the oldest dropping out at <paramref name="FreesAt"/>; <paramref name="LimitedAt"/> the
/// bank's latest refusal for its read limit.
/// </summary>
/// <paramref name="Warn"/>: « Synchroniser » says the bank refused within the last 24 hours, or Nid's count reached the limit (it never blocks).
public sealed record BankReadsView(
    int Unattended, int Attended, int Limit, Instant? FreesAt, Instant? LimitedAt, bool LimitedAttended, bool Warn)
{
    public static BankReadsView From(ReadCount c, Instant now)
        => new(c.Unattended, c.Attended, BankReads.UnattendedLimit, c.FreesAt, c.LimitedAt, c.LimitedAttended,
            c.LimitedAt > now - BankReads.Window || c.Unattended >= BankReads.UnattendedLimit);
}

/// <summary>
/// Issue #343, « Tester la connexion »: <paramref name="Outcome"/> is <c>accepted</c>, <c>rejected</c> (Enable Banking answered with
/// <paramref name="Code"/>, <c>enablebanking.&lt;ERROR&gt;</c>) or <c>unreachable</c> (no answer that could be read). Once accepted,
/// whether the application is active and whether its redirect URLs hold the one Nid sends.
/// </summary>
public sealed record BankingConnectionTestView(string Outcome, string? Code = null, bool? Active = null, bool? RedirectUrlAllowed = null);

public sealed record AspspView(string Name, string? Logo);

/// <summary>A connection waiting at the bank: the browser goes to <paramref name="RedirectUrl"/>.</summary>
public sealed record ConnectionStarted(Guid Id, string RedirectUrl);

/// <summary><paramref name="Status"/> is <c>active</c>, <c>expired</c>, <c>revoked</c> or <c>error</c>.</summary>
/// <param name="Reads">#365: its busiest account's reads and its latest refusal (<see cref="BankReads.Worst"/>).</param>
public sealed record ConnectionView(
    Guid Id, string AspspName, string Status, Instant? ValidUntil, Instant? LastSyncedAt, string? LastErrorCode,
    IReadOnlyList<BankAccountView> Accounts, BankReadsView Reads);

/// <summary>
/// A bank account as its reader sees it: <paramref name="LastBalance"/> a decimal string in <paramref name="Currency"/>,
/// <paramref name="Synced"/> while a connection still reads it, <paramref name="CanWrite"/> by the rules of <see cref="BankingAccess"/>.
/// <paramref name="Name"/> is the household's name when <paramref name="Renamed"/>, else <paramref name="BankName"/> (empty when the bank gave
/// none); <paramref name="CardDebit"/> is a card's <c>immediate</c> or <c>deferred</c> debit, when the bank says which; <paramref name="Holder"/>
/// and <paramref name="Product"/> the holder's name and the bank's product name, when it gave them (#353).
/// </summary>
public sealed record BankAccountView(
    Guid Id, string Name, string? IbanMasked, string Kind, string Currency, Guid? EnvelopeId, Guid? CardOfAccountId, bool AutoApplyBalance,
    bool Ignored, LocalDate? BalanceConflictOn, string? LastBalance, LocalDate? LastBalanceOn, bool Synced, bool CanWrite, string BankName,
    bool Renamed, string? CardDebit, string? Holder, string? Product, BankReadsView Reads, int? MinPayments = null, bool CountWithdrawals = false);

/// <summary>The account views of what <see cref="BankingAccess.ReadableAsync"/> lets the actor read, by name.</summary>
public sealed class BankAccountReader(
    BankingAccess access, IAccountStore accounts, IBalanceStore balances, IConnectionStore connections, IAccountReadLog reads, IClock clock)
{
    /// <summary>#365: each account's read count, from the reads kept; null accounts: every account.</summary>
    public async Task<IReadOnlyDictionary<Guid, ReadCount>> CountsAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        return (await reads.SinceAsync(ids, now - BankReads.Kept, ct)).GroupBy(r => r.Account).ToDictionary(g => g.Key, g => BankReads.Count(g, now));
    }

    public async Task<IReadOnlyList<BankAccountView>> ReadAsync(IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        var readable = await access.ReadableAsync(list, ct);
        var active = (await connections.ActiveAsync(ct)).Select(c => c.Id).ToHashSet();
        var ids = readable.Select(r => r.Account.Id).ToList();
        var counts = await CountsAsync(ids, ct);
        var latests = await balances.LatestManyAsync(ids, ct);
        var synced = (await accounts.SourcesAsync(ids, ct)).Where(s => active.Contains(s.Connection)).Select(s => s.Account).ToHashSet();
        var views = new List<BankAccountView>(readable.Count);
        foreach (var (account, canWrite) in readable)
        {
            var latest = latests.GetValueOrDefault(account.Id);
            views.Add(new BankAccountView(
                account.Id, account.Name, account.IbanMasked, account.Kind.ToString().ToLowerInvariant(), account.Currency.Code, account.Envelope?.Value,
                account.CardOf, account.AutoApplyBalance, account.Ignored, account.BalanceConflictOn,
                latest?.Amount.ToString("0.00", CultureInfo.InvariantCulture), latest?.AsOf, synced.Contains(account.Id), canWrite, account.BankName ?? account.Name,
                account.HouseholdName is not null, account.Debit?.ToString().ToLowerInvariant(), account.Holder, account.Product,
                BankReadsView.From(counts.GetValueOrDefault(account.Id, ReadCount.None), clock.GetCurrentInstant()), account.MinPayments, account.CountWithdrawals));
        }

        return views.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
    }
}
