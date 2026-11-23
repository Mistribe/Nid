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
    IHouseholdSettings settings, BankingOptions options, IAccessPolicy policy, IConnectionStore connections, IAccountStore accounts,
    IActorAccessor actor, IPersonRegistry persons, IItemRegistry items, IApplicationStore applications)
{
    public PersonId Person => actor.Current!.PersonId!.Value;

    private static bool Configured(HouseholdSettingsInfo household, BankingOptions options)
        => !string.IsNullOrWhiteSpace(household.EnableBankingAppId) && !string.IsNullOrWhiteSpace(options.PrivateKey);

    /// <summary>Whether the person can reach Enable Banking: their own application with a readable key, else the installation's.</summary>
    public async Task<bool> ConfiguredAsync(PersonId person, CancellationToken ct)
        => await applications.FindAsync(person, ct) is { } own ? own.KeyReadable : Configured(await settings.ReadAsync(ct), options);

    /// <summary>Whether any application exists to call Enable Banking with: the installation's, or an adult's own.</summary>
    public async Task<bool> AnyApplicationAsync(CancellationToken ct)
        => Configured(await settings.ReadAsync(ct), options) || (await applications.OwnersAsync(ct)).Count > 0;

    /// <summary>
    /// The application the actor's next call uses (list banks, start, status): banking on, then their own application when they
    /// declared one (an unreadable key is not configured), else the installation's.
    /// </summary>
    public async Task<Result<EnableBankingApp>> MyAppAsync(CancellationToken ct)
    {
        var household = await settings.ReadAsync(ct);
        if (!household.BankingEnabled)
            return Result.Failure<EnableBankingApp>(new ConflictFailure("Bank sync is turned off for this household.") { Code = BankingCodes.Disabled });
        return await applications.FindAsync(Person, ct) is not null
            ? await OwnAsync(Person, ct)
            : Installation(household);
    }

    /// <summary>
    /// The application a connection was made with, whoever asks: its creator's own when the id is theirs, else the installation's
    /// (legacy connections have no id); an application that is gone or unreadable is not configured. Banking being on is the caller's.
    /// </summary>
    public async Task<Result<EnableBankingApp>> AppForAsync(BankConnection connection, CancellationToken ct)
    {
        if (await applications.CredentialAsync(connection.Person, ct) is { } own && own.Id == connection.ApplicationId)
            return Result.Success(own);
        var household = await settings.ReadAsync(ct);
        return connection.ApplicationId is null || connection.ApplicationId == household.EnableBankingAppId
            ? Installation(household)
            : NotConfigured();
    }

    private async Task<Result<EnableBankingApp>> OwnAsync(PersonId person, CancellationToken ct)
        => await applications.CredentialAsync(person, ct) is { } credential ? Result.Success(credential) : NotConfigured();

    private Result<EnableBankingApp> Installation(HouseholdSettingsInfo household)
        => Configured(household, options) ? Result.Success(new EnableBankingApp(household.EnableBankingAppId!, options.PrivateKey!)) : NotConfigured();

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

    /// <summary>The accounts the actor reads, in order, each with whether they may write it, in a fixed number of queries.</summary>
    public async Task<IReadOnlyList<(BankAccount Account, bool CanWrite)>> ReadableAsync(IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        var rights = await RightsAsync(Person, await MineAsync(Person, ct), list, ct);
        return [.. list.Where(a => rights.ContainsKey(a.Id)).Select(a => (a, rights[a.Id]))];
    }

    /// <summary>
    /// The signed-up adults who read the account (D19, D22), or write it when <paramref name="kind"/> says so: its owner
    /// (<c>created_by</c>) first, whatever the actor.
    /// </summary>
    public async Task<IReadOnlyList<PersonId>> ReadersAsync(BankAccount account, CancellationToken ct, AccessKind kind = AccessKind.Read)
    {
        var readers = new List<PersonId>();
        foreach (var adult in (await AdultsAsync(ct)).OrderBy(a => a != account.CreatedBy))
            if ((await RightsAsync(adult, await MineAsync(adult, ct), [account], ct)).TryGetValue(account.Id, out var canWrite)
                && (canWrite || kind == AccessKind.Read))
                readers.Add(adult);
        return readers;
    }

    /// <summary>The readers of each account, as <see cref="ReadersAsync(BankAccount, CancellationToken, AccessKind)"/> gives them, the adults and their connections loaded once.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PersonId>>> ReadersAsync(IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        var adults = (await AdultsAsync(ct)).ToList();
        var rights = new Dictionary<PersonId, IReadOnlyDictionary<Guid, bool>>();
        foreach (var adult in adults)
            rights[adult] = await RightsAsync(adult, await MineAsync(adult, ct), list, ct);
        var readers = new Dictionary<Guid, IReadOnlyList<PersonId>>();
        foreach (var account in list)
            readers[account.Id] = [.. adults.OrderBy(a => a != account.CreatedBy).Where(a => rights[a].ContainsKey(account.Id))];
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
        var mine = await MineAsync(Person, ct);
        return account is null || !(await RightsAsync(Person, mine, [account], ct)).TryGetValue(account.Id, out var canWrite)
            ? Fail<BankAccount>(BankingCodes.AccountNotFound, "No such bank account.")
            : access == AccessKind.Write && !canWrite
                ? Result.Failure<BankAccount>(new UnauthorizedFailure("You can read this bank account but not change it.", null))
                : Result.Success(account);
    }

    private async Task<IReadOnlySet<Guid>> MineAsync(PersonId person, CancellationToken ct)
        => (await connections.MineAsync(person, ct)).Select(c => c.Id).ToHashSet();

    /// <summary>
    /// The accounts of <paramref name="list"/> <paramref name="person"/> reads, each with whether they may write it (an absent one is
    /// not read), in a fixed number of queries whatever the number of accounts (architecture §5.3.2).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, bool>> RightsAsync(
        PersonId person, IReadOnlySet<Guid> mine, IReadOnlyList<BankAccount> list, CancellationToken ct)
    {
        // A card follows its account; one whose account is gone rules itself.
        var parents = list.Select(a => a.CardOf).OfType<Guid>().Distinct().ToList();
        var found = parents.Count == 0 ? [] : (await accounts.FindManyAsync(parents, ct)).ToDictionary(a => a.Id);
        var ruling = list.DistinctBy(a => a.Id)
            .ToDictionary(a => a.Id, a => a.CardOf is { } parent && found.TryGetValue(parent, out var account) ? account : a);

        // On an envelope: its writers write the account, its other readers read it.
        var envelopes = ruling.Values.Select(r => r.Envelope).OfType<ItemId>().Distinct().ToList();
        var writes = await policy.CheckManyAsync(person, envelopes, AccessKind.Write, ct);
        var reads = await policy.CheckManyAsync(person, [.. envelopes.Where(e => !writes.Contains(e))], AccessKind.Read, ct);

        // Off an envelope: its creator and the creators of the connections that see it own it (#354's managing adult needs an envelope).
        var others = ruling.Values.Where(r => r.Envelope is null && r.CreatedBy != person).Select(r => r.Id).Distinct().ToList();
        var seen = others.Count == 0
            ? []
            : (await accounts.SourcesAsync(others, ct)).Where(s => mine.Contains(s.Connection)).Select(s => s.Account).ToHashSet();

        var rights = new Dictionary<Guid, bool>();
        foreach (var (id, r) in ruling)
            if (r.Envelope is { } envelope)
            {
                if (writes.Contains(envelope))
                    rights[id] = true;
                else if (reads.Contains(envelope))
                    rights[id] = false;
            }
            else if (r.CreatedBy == person || seen.Contains(r.Id))
                rights[id] = true;
        return rights;
    }

    /// <summary>
    /// Whether the account is the actor's own: they created it or a connection of theirs reads it. Only its owner moves it to another
    /// envelope or account, which would take it from the other adult's sight.
    /// </summary>
    public async Task<bool> OwnsAsync(BankAccount account, CancellationToken ct)
    {
        var mine = await MineAsync(Person, ct);
        return account.CreatedBy == Person || (await accounts.SourcesAsync(account.Id, ct)).Any(s => mine.Contains(s.Connection))
            || await ManagesAsync(Person, account, ct);
    }

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
