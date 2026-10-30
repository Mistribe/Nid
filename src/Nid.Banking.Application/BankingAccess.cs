using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application;

/// <summary>
/// The switch and the access rules of bank sync (D7, D22), in one place for every banking handler: a connection is its creator's
/// only; an account on an envelope is read by the envelope's readers and written by its writers; a card attached to an account follows
/// that account; any other account belongs to the person whose connection first saw it and to the creators of the connections that
/// see it now.
/// </summary>
public sealed class BankingAccess(
    IHouseholdSettings settings, BankingOptions options, IAccessPolicy policy, IConnectionStore connections, IAccountStore accounts,
    IActorAccessor actor)
{
    public PersonId Person => actor.Current!.PersonId!.Value;

    public static bool Configured(HouseholdSettingsInfo household, BankingOptions options)
        => !string.IsNullOrWhiteSpace(household.EnableBankingAppId) && !string.IsNullOrWhiteSpace(options.PrivateKey);

    /// <summary>The application id, when banking is on and configured: every request that reaches a bank asks it first.</summary>
    public async Task<Result<string>> AppIdAsync(CancellationToken ct)
    {
        var household = await settings.ReadAsync(ct);
        if (!household.BankingEnabled)
            return Fail<string>(BankingCodes.Disabled, "Bank sync is turned off for this household.");
        return Configured(household, options)
            ? Result.Success(household.EnableBankingAppId!)
            : Fail<string>(BankingCodes.NotConfigured, "Bank sync has no Enable Banking application id or private key.");
    }

    /// <summary>Banking on, whatever Enable Banking's configuration: what file imports and the accounts they make need (ruling R-d).</summary>
    public async Task<Result> EnabledAsync(CancellationToken ct)
        => (await settings.ReadAsync(ct)).BankingEnabled
            ? Result.Success()
            : Result.Failure(new Failure(BankingCodes.Disabled, "Bank sync is turned off for this household.", null));

    /// <summary>One of the actor's connections, past its first callback: a pending one is nobody's to act on yet.</summary>
    public async Task<Result<BankConnection>> MyConnectionAsync(Guid id, CancellationToken ct)
        => (await connections.MineAsync(Person, ct)).FirstOrDefault(c => c.Id == id && c.Status != ConnectionStatus.Pending) is { } found
            ? Result.Success(found)
            : Fail<BankConnection>(BankingCodes.ConnectionNotFound, "No such bank connection.");

    /// <summary>The accounts the actor reads, in order, each with whether they may write it.</summary>
    public async Task<IReadOnlyList<(BankAccount Account, bool CanWrite)>> ReadableAsync(IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        var mine = (await connections.MineAsync(Person, ct)).Select(c => c.Id).ToHashSet();
        var readable = new List<(BankAccount, bool)>();
        foreach (var account in list)
            if (await CanWriteAsync(account, mine, ct) is { } canWrite)
                readable.Add((account, canWrite));
        return readable;
    }

    /// <summary>
    /// The account, when the actor may <paramref name="access"/> it: <see cref="BankingCodes.AccountNotFound"/> when they cannot read it
    /// (its existence is not disclosed), forbidden when they only read it.
    /// </summary>
    public async Task<Result<BankAccount>> AccountAsync(Guid id, AccessKind access, CancellationToken ct)
    {
        var account = await accounts.FindAsync(id, ct);
        var mine = (await connections.MineAsync(Person, ct)).Select(c => c.Id).ToHashSet();
        return account is null || await CanWriteAsync(account, mine, ct) is not { } canWrite
            ? Fail<BankAccount>(BankingCodes.AccountNotFound, "No such bank account.")
            : access == AccessKind.Write && !canWrite
                ? Result.Failure<BankAccount>(new UnauthorizedFailure("You can read this bank account but not change it.", null))
                : Result.Success(account);
    }

    /// <summary><c>null</c> when the actor cannot read the account, else whether they may write it.</summary>
    private async Task<bool?> CanWriteAsync(BankAccount account, IReadOnlySet<Guid> mine, CancellationToken ct)
    {
        var ruling = account.CardOf is { } parent && await accounts.FindAsync(parent, ct) is { } found ? found : account;
        if (ruling.Envelope is { } envelope)
        {
            // Write answers « not found » to a non-reader and « unauthorized » to a reader who does not write.
            var write = await policy.CheckAsync(Person, envelope, AccessKind.Write, ct);
            return write.TryGetFailure(out var failure) ? failure is UnauthorizedFailure ? false : null : true;
        }

        return await OwnsAsync(ruling, mine, ct) ? true : null;
    }

    /// <summary>
    /// Whether the account is the actor's own: they created it or a connection of theirs reads it. Only its owner moves it to another
    /// envelope or account, which would take it from the other adult's sight.
    /// </summary>
    public async Task<bool> OwnsAsync(BankAccount account, CancellationToken ct)
        => await OwnsAsync(account, (await connections.MineAsync(Person, ct)).Select(c => c.Id).ToHashSet(), ct);

    private async Task<bool> OwnsAsync(BankAccount account, IReadOnlySet<Guid> mine, CancellationToken ct)
        => account.CreatedBy == Person || (await accounts.SourcesAsync(account.Id, ct)).Any(s => mine.Contains(s.Connection));

    internal static Result<T> Fail<T>(string code, string message) where T : notnull => Result.Failure<T>(new Failure(code, message, null));

    /// <summary>The failure of <paramref name="result"/>, as a result of a type.</summary>
    internal static Result<T> Failed<T>(Result result) where T : notnull
        => Result.Failure<T>(result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

    /// <summary>The failure of <paramref name="result"/>, as a result of another type.</summary>
    internal static Result<T> Failed<T, S>(Result<S> result) where T : notnull where S : notnull
        => Result.Failure<T>(result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
}
