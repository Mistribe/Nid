using Nid.Banking.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.TestSupport;

/// <summary>
/// A scripted <see cref="IEnableBankingClient"/>: tests fill <see cref="Aspsps"/>, <see cref="Sessions"/> (by authorisation code),
/// <see cref="Balances"/> and <see cref="BalanceFailures"/> (by session and account uid) and read what was asked back from the records.
/// Anything not scripted is a failure; never touches the network.
/// </summary>
public sealed class FakeEnableBanking : IEnableBankingClient
{
    public sealed record StartAuthCall(string AppId, Aspsp Aspsp, Instant ValidUntil, string State, string RedirectUrl);

    public sealed record BalancesCall(string AppId, string SessionId, string AccountUid, PsuHeaders? Psu);

    public List<Aspsp> Aspsps { get; } = [];

    public Dictionary<string, AuthorizedSession> Sessions { get; } = [];

    public Dictionary<(string SessionId, string AccountUid), IReadOnlyList<ProviderBalance>> Balances { get; } = [];

    /// <summary>An API error code (<c>enablebanking.EXPIRED_SESSION</c>…) per session and account uid; wins over <see cref="Balances"/>.</summary>
    public Dictionary<(string SessionId, string AccountUid), string> BalanceFailures { get; } = [];

    /// <summary>When set, every call fails with this code.</summary>
    public string? FailWith { get; set; }

    public List<string> Countries { get; } = [];

    public List<StartAuthCall> StartAuths { get; } = [];

    public List<string> CodesExchanged { get; } = [];

    public List<BalancesCall> BalanceCalls { get; } = [];

    public List<string> DeletedSessions { get; } = [];

    /// <summary>What every test host uses unless a test scripts its own: every call is a provider error.</summary>
    public static FakeEnableBanking Offline() => new() { FailWith = BankingCodes.ProviderError };

    public Task<Result<IReadOnlyList<Aspsp>>> AspspsAsync(string appId, string country, CancellationToken ct)
    {
        Record(Countries, country);
        return Task.FromResult(FailWith is { } code
            ? Fail<IReadOnlyList<Aspsp>>(code)
            : Result.Success<IReadOnlyList<Aspsp>>(Aspsps.Where(a => a.Country == country).ToList()));
    }

    public Task<Result<AuthStart>> StartAuthAsync(string appId, Aspsp aspsp, Instant validUntil, string state, string redirectUrl, CancellationToken ct)
    {
        Record(StartAuths, new StartAuthCall(appId, aspsp, validUntil, state, redirectUrl));
        return Task.FromResult(FailWith is { } code ? Fail<AuthStart>(code) : Result.Success(new AuthStart($"https://bank.example/auth?state={state}")));
    }

    public Task<Result<AuthorizedSession>> CreateSessionAsync(string appId, string code, CancellationToken ct)
    {
        Record(CodesExchanged, code);
        return Task.FromResult(FailWith is { } fail ? Fail<AuthorizedSession>(fail)
            : Sessions.TryGetValue(code, out var session) ? Result.Success(session) : Fail<AuthorizedSession>("enablebanking.WRONG_AUTHORIZATION_CODE"));
    }

    public Task<Result<IReadOnlyList<ProviderBalance>>> BalancesAsync(string appId, string sessionId, string accountUid, PsuHeaders? psu, CancellationToken ct)
    {
        Record(BalanceCalls, new BalancesCall(appId, sessionId, accountUid, psu));
        var key = (sessionId, accountUid);
        return Task.FromResult(FailWith is { } fail ? Fail<IReadOnlyList<ProviderBalance>>(fail)
            : BalanceFailures.TryGetValue(key, out var code) ? Fail<IReadOnlyList<ProviderBalance>>(code)
            : Balances.TryGetValue(key, out var balances) ? Result.Success(balances) : Fail<IReadOnlyList<ProviderBalance>>("enablebanking.ACCOUNT_DOES_NOT_EXIST"));
    }

    public Task<Result> DeleteSessionAsync(string appId, string sessionId, CancellationToken ct)
    {
        Record(DeletedSessions, sessionId);
        return Task.FromResult(FailWith is { } code ? Result.Failure(new Failure(code, "Fake provider failure.", null)) : Result.Success());
    }

    private static void Record<T>(List<T> list, T item)
    {
        lock (list)
            list.Add(item);
    }

    private static Result<T> Fail<T>(string code) where T : notnull => Result.Failure<T>(new Failure(code, "Fake provider failure.", null));
}
