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
/// see it now. An account on a dependent's envelope is that child's, which every adult who writes the envelope manages (#354).
/// </summary>
public sealed class BankingAccess(
    IHouseholdSettings settings, IAccessPolicy policy, IConnectionStore connections, IAccountStore accounts,
    IActorAccessor actor, IPersonRegistry persons, IItemRegistry items, IApplicationStore applications)
{
    public PersonId Person => actor.Current!.PersonId!.Value;

    /// <summary>Whether the person can reach Enable Banking: their own application, with a readable key (#414: there is no other).</summary>
    public async Task<bool> ConfiguredAsync(PersonId person, CancellationToken ct)
        => await applications.FindAsync(person, ct) is { KeyReadable: true };

    /// <summary>Whether any adult declared an application to call Enable Banking with.</summary>
    public async Task<bool> AnyApplicationAsync(CancellationToken ct) => (await applications.OwnersAsync(ct)).Count > 0;

    /// <summary>
    /// The application the actor's next call uses (list banks, start, status): banking on, then their own application; none declared or
    /// an unreadable key is not configured.
    /// </summary>
    public async Task<Result<EnableBankingApp>> MyAppAsync(CancellationToken ct)
    {
        if (!(await settings.ReadAsync(ct)).BankingEnabled)
            return Result.Failure<EnableBankingApp>(new ConflictFailure("Bank sync is turned off for this household.") { Code = BankingCodes.Disabled });
        return await applications.CredentialAsync(Person, ct) is { } own ? Result.Success(own) : NotConfigured();
    }

    /// <summary>
    /// The application a connection was made with, whoever asks: its creator's own when the id is theirs; any other (another adult's, one
    /// replaced since) is not configured, nor is an unreadable key. Banking being on is the caller's.
    /// </summary>
    public async Task<Result<EnableBankingApp>> AppForAsync(BankConnection connection, CancellationToken ct)
        => await applications.CredentialAsync(connection.Person, ct) is { } own && own.Id == connection.ApplicationId
            ? Result.Success(own)
            : NotConfigured();

    private static Result<EnableBankingApp> NotConfigured()
        => Result.Failure<EnableBankingApp>(new ConflictFailure("Bank sync has no usable Enable Banking application.") { Code = BankingCodes.NotConfigured });

    /// <summary>Banking on, whatever Enable Banking's configuration: what file imports and the accounts they make need (ruling R-d).</summary>
    public async Task<Result> EnabledAsync(CancellationToken ct)
        => (await settings.ReadAsync(ct)).BankingEnabled
            ? Result.Success()
            : Result.Failure(new ConflictFailure("Bank sync is turned off for this household.") { Code = BankingCodes.Disabled });

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
            if (await CanWriteAsync(Person, account, mine, ct) is { } canWrite)
                readable.Add((account, canWrite));
        return readable;
    }

    /// <summary>
    /// The signed-up adults who read the account (D19, D22), or write it when <paramref name="kind"/> says so: its owner
    /// (<c>created_by</c>) first, whatever the actor.
    /// </summary>
    public async Task<IReadOnlyList<PersonId>> ReadersAsync(BankAccount account, CancellationToken ct, AccessKind kind = AccessKind.Read)
    {
        var readers = new List<PersonId>();
        foreach (var adult in (await AdultsAsync(ct)).OrderBy(a => a != account.CreatedBy))
            if (await CanWriteAsync(adult, account, (await connections.MineAsync(adult, ct)).Select(c => c.Id).ToHashSet(), ct) is { } canWrite
                && (canWrite || kind == AccessKind.Read))
                readers.Add(adult);
        return readers;
    }

    /// <summary>The readers of each account, as <see cref="ReadersAsync(BankAccount, CancellationToken, AccessKind)"/> gives them, the adults and their connections loaded once.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PersonId>>> ReadersAsync(IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        var adults = (await AdultsAsync(ct)).ToList();
        var mine = new Dictionary<PersonId, IReadOnlySet<Guid>>();
        foreach (var adult in adults)
            mine[adult] = (await connections.MineAsync(adult, ct)).Select(c => c.Id).ToHashSet();
        var readers = new Dictionary<Guid, IReadOnlyList<PersonId>>();
        foreach (var account in list)
        {
            var found = new List<PersonId>();
            foreach (var adult in adults.OrderBy(a => a != account.CreatedBy))
                if (await CanWriteAsync(adult, account, mine[adult], ct) is not null)
                    found.Add(adult);
            readers[account.Id] = found;
        }
        return readers;
    }

    /// <summary>
    /// §0 n°1: whether every adult who reads <paramref name="item"/> reads the account too. Only then may a proposal on the item tell of
    /// one of the account's lines: the item's readers see it and are asked to decide.
    /// </summary>
    public async Task<bool> ItemReadersReadAsync(Guid account, ItemId item, CancellationToken ct)
    {
        if (await accounts.FindAsync(account, ct) is not { } found)
            return false;
        var readers = await ReadersAsync(found, ct);
        foreach (var adult in (await AdultsAsync(ct)).Where(a => !readers.Contains(a)))
            if ((await policy.CheckAsync(adult, item, AccessKind.Read, ct)).IsSuccess)
                return false;
        return true;
    }

    /// <summary>Every adult, a pending one (not signed in yet) included: he reads nothing, so he keeps an account from being « shared ».</summary>
    public async Task<int> AdultCountAsync(CancellationToken ct) => (await persons.ListAsync(ct)).Count(p => p.Kind == PersonKind.Adult);

    /// <summary>
    /// Whether every adult (<paramref name="adults"/>, from <see cref="AdultCountAsync"/>) reads the account: a rule made from one
    /// of its lines then needs no account of its own.
    /// </summary>
    public async Task<bool> SharedAsync(BankAccount account, int adults, CancellationToken ct) => (await ReadersAsync(account, ct)).Count == adults;

    private async Task<IEnumerable<PersonId>> AdultsAsync(CancellationToken ct)
        => (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult && !p.IsPending).Select(p => p.Id);

    /// <summary>
    /// The account, when the actor may <paramref name="access"/> it: <see cref="BankingCodes.AccountNotFound"/> when they cannot read it
    /// (its existence is not disclosed), forbidden when they only read it.
    /// </summary>
    public async Task<Result<BankAccount>> AccountAsync(Guid id, AccessKind access, CancellationToken ct)
    {
        var account = await accounts.FindAsync(id, ct);
        var mine = (await connections.MineAsync(Person, ct)).Select(c => c.Id).ToHashSet();
        return account is null || await CanWriteAsync(Person, account, mine, ct) is not { } canWrite
            ? Fail<BankAccount>(BankingCodes.AccountNotFound, "No such bank account.")
            : access == AccessKind.Write && !canWrite
                ? Result.Failure<BankAccount>(new UnauthorizedFailure("You can read this bank account but not change it.", null))
                : Result.Success(account);
    }

    /// <summary><c>null</c> when <paramref name="person"/> cannot read the account, else whether they may write it.</summary>
    private async Task<bool?> CanWriteAsync(PersonId person, BankAccount account, IReadOnlySet<Guid> mine, CancellationToken ct)
    {
        var ruling = account.CardOf is { } parent && await accounts.FindAsync(parent, ct) is { } found ? found : account;
        if (ruling.Envelope is { } envelope)
        {
            // Write answers « not found » to a non-reader and « unauthorized » to a reader who does not write.
            var write = await policy.CheckAsync(person, envelope, AccessKind.Write, ct);
            return write.TryGetFailure(out var failure) ? failure is UnauthorizedFailure ? false : null : true;
        }

        return await OwnsAsync(person, ruling, mine, ct) ? true : null;
    }

    /// <summary>
    /// Whether the account is the actor's own: they created it or a connection of theirs reads it. Only its owner moves it to another
    /// envelope or account, which would take it from the other adult's sight.
    /// </summary>
    public async Task<bool> OwnsAsync(BankAccount account, CancellationToken ct)
        => await OwnsAsync(Person, account, (await connections.MineAsync(Person, ct)).Select(c => c.Id).ToHashSet(), ct);

    private async Task<bool> OwnsAsync(PersonId person, BankAccount account, IReadOnlySet<Guid> mine, CancellationToken ct)
        => account.CreatedBy == person || (await accounts.SourcesAsync(account.Id, ct)).Any(s => mine.Contains(s.Connection))
            || await ManagesAsync(person, account, ct);

    /// <summary>
    /// #354: an account on an envelope only dependents own (a card: its account's) is the child's, not its connector's: whoever writes that
    /// envelope moves it as its owner would. One connection is enough for both adults.
    /// </summary>
    private async Task<bool> ManagesAsync(PersonId person, BankAccount account, CancellationToken ct)
    {
        var ruling = account.CardOf is { } parent && await accounts.FindAsync(parent, ct) is { } found ? found : account;
        return ruling.Envelope is { } envelope
            && await items.FindAsync(envelope, ct) is { Owners: [_, ..] owners } && owners.All(o => o.Kind == PersonKind.Dependent)
            && (await policy.CheckAsync(person, envelope, AccessKind.Write, ct)).IsSuccess;
    }

    internal static Result<T> Fail<T>(string code, string message) where T : notnull => Result.Failure<T>(new Failure(code, message, null));

    /// <summary>The failure of <paramref name="result"/>, as a result of a type.</summary>
    internal static Result<T> Failed<T>(Result result) where T : notnull
        => Result.Failure<T>(result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

    /// <summary>The failure of <paramref name="result"/>, as a result of another type.</summary>
    internal static Result<T> Failed<T, S>(Result<S> result) where T : notnull where S : notnull
        => Result.Failure<T>(result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
}
