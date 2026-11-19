using System.Globalization;
using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Application;

/// <summary>
/// Whether bank sync is on, and whether the administrator gave it an application id and the server a private key. For an administrator
/// only (issue #343), <paramref name="AppIdSaved"/> and <paramref name="KeyStatus"/> (<see cref="BankingKeyStatus"/>) say which is missing.
/// </summary>
public sealed record BankingStatusView(bool Enabled, bool Configured, bool? AppIdSaved = null, string? KeyStatus = null);

/// <summary>
/// Issue #343, Administration › Banque: the key's <see cref="BankingKeyStatus"/> (never the key), whether an application id is saved, and
/// the redirect URL Nid sends the banks to, which the application must declare.
/// </summary>
public sealed record BankingDiagnosticsView(string KeyStatus, bool AppIdSaved, string RedirectUrl);

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
public sealed record ConnectionView(
    Guid Id, string AspspName, string Status, DateTimeOffset? ValidUntil, DateTimeOffset? LastSyncedAt, string? LastErrorCode,
    IReadOnlyList<BankAccountView> Accounts);

/// <summary>
/// A bank account as its reader sees it: <paramref name="LastBalance"/> a decimal string in <paramref name="Currency"/>,
/// <paramref name="Synced"/> while a connection still reads it, <paramref name="CanWrite"/> by the rules of <see cref="BankingAccess"/>.
/// </summary>
public sealed record BankAccountView(
    Guid Id, string Name, string? IbanMasked, string Kind, string Currency, Guid? EnvelopeId, Guid? CardOfAccountId, bool AutoApplyBalance,
    bool Ignored, LocalDate? BalanceConflictOn, string? LastBalance, LocalDate? LastBalanceOn, bool Synced, bool CanWrite);

/// <summary>The account views of what <see cref="BankingAccess.ReadableAsync"/> lets the actor read, by name.</summary>
public sealed class BankAccountReader(BankingAccess access, IAccountStore accounts, IBalanceStore balances, IConnectionStore connections)
{
    public async Task<IReadOnlyList<BankAccountView>> ReadAsync(IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        var readable = await access.ReadableAsync(list, ct);
        var active = (await connections.ActiveAsync(ct)).Select(c => c.Id).ToHashSet();
        var views = new List<BankAccountView>(readable.Count);
        foreach (var (account, canWrite) in readable)
        {
            var latest = await balances.LatestAsync(account.Id, ct);
            var synced = (await accounts.SourcesAsync(account.Id, ct)).Any(s => active.Contains(s.Connection));
            views.Add(new BankAccountView(
                account.Id, account.Name, account.IbanMasked, account.Kind.ToString().ToLowerInvariant(), account.Currency.Code, account.Envelope?.Value,
                account.CardOf, account.AutoApplyBalance, account.Ignored, account.BalanceConflictOn,
                latest?.Amount.ToString("0.00", CultureInfo.InvariantCulture), latest?.AsOf, synced, canWrite));
        }

        return views.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
    }
}
