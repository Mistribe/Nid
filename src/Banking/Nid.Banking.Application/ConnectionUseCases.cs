using System.Security.Cryptography;
using System.Text;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using Duration = NodaTime.Duration;

namespace Nid.Banking.Application;

public sealed record GetBankingStatusQuery : IQuery<BankingStatusView>;

public sealed record ListAspspsQuery : IQuery<IReadOnlyList<AspspView>>;

/// <summary>
/// Spec §3.1: asks the bank for the actor's consent. <paramref name="RedirectUrl"/> is the callback the administrator registered at
/// Enable Banking (<c>{Auth:PublicUrl}/api/v1/banking/callback</c>), set by the route.
/// </summary>
public sealed record StartConnectionCommand(string AspspName, string RedirectUrl) : ICommand<ConnectionStarted>, IBrowserOnly;

/// <summary>Spec §3.3: the consent asked again for the same connection; its accounts are found again by their identification hash.</summary>
public sealed record RenewConnectionCommand(Guid Id, string RedirectUrl) : ICommand<ConnectionStarted>, IBrowserOnly;

/// <summary>The bank's callback: <paramref name="Error"/> when the person refused or cancelled, else the code to exchange for a session.</summary>
public sealed record CompleteConnectionCommand(string State, string? Code, string? Error) : ICommand<Guid>, IBrowserOnly;

/// <summary>The actor's connections, without those still waiting for their first callback.</summary>
public sealed record ListMyConnectionsQuery : IQuery<IReadOnlyList<ConnectionView>>;

/// <summary>Spec §8: revokes the session at the bank and forgets the connection; its accounts stay, no longer synchronised.</summary>
public sealed record DeleteConnectionCommand(Guid Id) : ICommand<Guid>, IBrowserOnly;

/// <summary>A started authorisation: the state the bank sends back is 32 random bytes, of which only the SHA-256 is kept.</summary>
internal static class BankAuthorization
{
    private static readonly Duration LongestConsent = Duration.FromDays(180);

    public static byte[] Hash(string state) => SHA256.HashData(Encoding.UTF8.GetBytes(state));

    public static async Task<Result<(string Url, byte[] StateHash)>> StartAsync(
        IEnableBankingClient client, string appId, Aspsp aspsp, string redirectUrl, Instant now, CancellationToken ct)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var validUntil = now + (aspsp.MaximumConsentSeconds is { } seconds ? Duration.FromSeconds(seconds) : LongestConsent);
        return (await client.StartAuthAsync(appId, aspsp, validUntil, state, redirectUrl, ct)).Map(started => (started.Url, Hash(state)));
    }
}

[RequestHandler<GetBankingStatusQuery, BankingStatusView>]
public sealed class GetBankingStatusHandler(IHouseholdSettings settings, BankingOptions options)
    : IRequestHandler<GetBankingStatusQuery, BankingStatusView>
{
    public async ValueTask<Result<BankingStatusView>> HandleAsync(GetBankingStatusQuery query, CancellationToken ct = default)
    {
        var household = await settings.ReadAsync(ct);
        return Result.Success(new BankingStatusView(household.BankingEnabled, BankingAccess.Configured(household, options)));
    }
}

[RequestHandler<ListAspspsQuery, IReadOnlyList<AspspView>>]
public sealed class ListAspspsHandler(BankingAccess access, AspspCache aspsps) : IRequestHandler<ListAspspsQuery, IReadOnlyList<AspspView>>
{
    public async ValueTask<Result<IReadOnlyList<AspspView>>> HandleAsync(ListAspspsQuery query, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.TryGetValue(out var appId))
            return BankingAccess.Failed<IReadOnlyList<AspspView>, string>(app);
        return (await aspsps.FrenchAsync(appId!, ct)).Map(banks => (IReadOnlyList<AspspView>)banks.Select(b => new AspspView(b.Name, b.Logo)).ToList());
    }
}

[RequestHandler<StartConnectionCommand, ConnectionStarted>]
public sealed class StartConnectionHandler(
    BankingAccess access, AspspCache aspsps, IConnectionStore connections, IEnableBankingClient client, BankingOptions options, IClock clock)
    : IRequestHandler<StartConnectionCommand, ConnectionStarted>
{
    public async ValueTask<Result<ConnectionStarted>> HandleAsync(StartConnectionCommand command, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.TryGetValue(out var appId))
            return BankingAccess.Failed<ConnectionStarted, string>(app);
        var banks = await aspsps.FrenchAsync(appId!, ct);
        if (!banks.TryGetValue(out var list))
            return BankingAccess.Failed<ConnectionStarted, IReadOnlyList<Aspsp>>(banks);
        if (list!.FirstOrDefault(b => b.Name == command.AspspName) is not { } aspsp)
            return BankingAccess.Fail<ConnectionStarted>(BankingCodes.UnknownAspsp, "Enable Banking lists no such French bank.");

        var now = clock.GetCurrentInstant();
        var stateTime = Duration.FromMinutes(options.StateMinutes);
        // The person's flows abandoned at the bank, or refused at their callback, are dead by now.
        await connections.DeletePendingAsync(access.Person, now - stateTime, ct);
        var started = await BankAuthorization.StartAsync(client, appId!, aspsp, command.RedirectUrl, now, ct);
        if (!started.TryGetValue(out var auth))
            return BankingAccess.Failed<ConnectionStarted, (string, byte[])>(started);
        var id = await connections.CreatePendingAsync(access.Person, aspsp.Name, aspsp.Country, auth.StateHash, now + stateTime, ct);
        return Result.Success(new ConnectionStarted(id, auth.Url));
    }
}

[RequestHandler<RenewConnectionCommand, ConnectionStarted>]
public sealed class RenewConnectionHandler(
    BankingAccess access, AspspCache aspsps, IConnectionStore connections, IEnableBankingClient client, BankingOptions options, IClock clock)
    : IRequestHandler<RenewConnectionCommand, ConnectionStarted>
{
    public async ValueTask<Result<ConnectionStarted>> HandleAsync(RenewConnectionCommand command, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.TryGetValue(out var appId))
            return BankingAccess.Failed<ConnectionStarted, string>(app);
        var mine = await access.MyConnectionAsync(command.Id, ct);
        if (!mine.TryGetValue(out var connection))
            return BankingAccess.Failed<ConnectionStarted, BankConnection>(mine);
        var banks = await aspsps.FrenchAsync(appId!, ct);
        if (!banks.TryGetValue(out var list))
            return BankingAccess.Failed<ConnectionStarted, IReadOnlyList<Aspsp>>(banks);
        if (list!.FirstOrDefault(b => b.Name == connection!.AspspName && b.Country == connection.AspspCountry) is not { } aspsp)
            return BankingAccess.Fail<ConnectionStarted>(BankingCodes.UnknownAspsp, "Enable Banking no longer lists this bank.");

        var now = clock.GetCurrentInstant();
        var started = await BankAuthorization.StartAsync(client, appId!, aspsp, command.RedirectUrl, now, ct);
        if (!started.TryGetValue(out var auth))
            return BankingAccess.Failed<ConnectionStarted, (string, byte[])>(started);
        await connections.SetStateAsync(connection!.Id, auth.StateHash, now + Duration.FromMinutes(options.StateMinutes), ct);
        return Result.Success(new ConnectionStarted(connection.Id, auth.Url));
    }
}

/// <summary>
/// Spec §3.1: the state is found and used up first, committed on its own, so a replay fails even when the code exchange then does; a
/// state started by someone else is refused like an unknown one. Accounts in a currency that is no ISO code are left out.
/// </summary>
[RequestHandler<CompleteConnectionCommand, Guid>]
public sealed class CompleteConnectionHandler(
    BankingAccess access, IConnectionStore connections, IAccountStore accounts, IEnableBankingClient client, IAccessPolicy policy, IUnitOfWork unitOfWork)
    : IRequestHandler<CompleteConnectionCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(CompleteConnectionCommand command, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.TryGetValue(out var appId))
            return BankingAccess.Failed<Guid, string>(app);
        var connection = await connections.FindByStateAsync(BankAuthorization.Hash(command.State), ct);
        if (connection is null || connection.Person != access.Person)
            return BankingAccess.Fail<Guid>(BankingCodes.StateInvalid, "This bank callback is unknown, used, expired or someone else's.");
        if (command.Error is not null || string.IsNullOrEmpty(command.Code))
            return BankingAccess.Fail<Guid>(BankingCodes.AuthRefused, "The bank refused or cancelled the authorisation.");

        var superseded = connection.Status == ConnectionStatus.Pending ? null : await connections.SessionIdAsync(connection.Id, ct);
        var created = await client.CreateSessionAsync(appId!, command.Code, ct);
        if (!created.TryGetValue(out var session))
            return BankingAccess.Failed<Guid, AuthorizedSession>(created);

        await using (var transaction = await unitOfWork.BeginAsync(ct))
        {
            await connections.ActivateAsync(connection.Id, session!.SessionId, session.ValidUntil, ct);
            var seen = await accounts.UpsertFromSessionAsync(connection.Id,
                session.Accounts.Where(a => Currency.TryCreate(a.Currency.ToUpperInvariant(), out _)).ToList(), ct);
            // A bank connection outranks a statement file: an account another adult imported becomes the connecting person's, and leaves
            // an envelope they cannot read, which would hide their own account from them (D7).
            foreach (var found in seen)
            {
                if (!await accounts.ClaimFromFileAsync(found.Id, access.Person, ct))
                    continue;
                var account = found with { AutoApplyBalance = false, Ignored = false }; // as the claim left it
                if (account.Envelope is { } envelope && !(await policy.CheckAsync(access.Person, envelope, AccessKind.Read, ct)).IsSuccess
                    && (await accounts.UpdateAsync(account with { Envelope = null, AssociatedOn = null, AssociatedBy = null }, ct)).TryGetFailure(out var failure))
                    return Result.Failure<Guid>(failure!);
                // A card follows its parent's access: the other adult's own card does not go with the claimed account.
                foreach (var card in (await accounts.AllAsync(ct)).Where(c => c.CardOf == account.Id))
                    if (!await access.OwnsAsync(card, ct) && (await accounts.UpdateAsync(card with { CardOf = null }, ct)).TryGetFailure(out var detach))
                        return Result.Failure<Guid>(detach!);
            }
            await transaction.CommitAsync(ct);
        }

        // A renewal's previous consent is revoked at the bank; it is dead weight there, and a failure here changes nothing for Nid.
        if (superseded is not null && superseded != session.SessionId)
            await client.DeleteSessionAsync(appId!, superseded, ct);
        return Result.Success(connection.Id);
    }
}

[RequestHandler<ListMyConnectionsQuery, IReadOnlyList<ConnectionView>>]
public sealed class ListMyConnectionsHandler(BankingAccess access, IConnectionStore connections, IAccountStore accounts, BankAccountReader reader)
    : IRequestHandler<ListMyConnectionsQuery, IReadOnlyList<ConnectionView>>
{
    public async ValueTask<Result<IReadOnlyList<ConnectionView>>> HandleAsync(ListMyConnectionsQuery query, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.IsSuccess)
            return BankingAccess.Failed<IReadOnlyList<ConnectionView>, string>(app);

        var views = new List<ConnectionView>();
        foreach (var c in (await connections.MineAsync(access.Person, ct)).Where(c => c.Status != ConnectionStatus.Pending))
            views.Add(new ConnectionView(
                c.Id, c.AspspName, c.Status.ToString().ToLowerInvariant(), c.ValidUntil?.ToDateTimeOffset(), c.LastSyncedAt?.ToDateTimeOffset(),
                c.LastErrorCode, await reader.ReadAsync(await accounts.ForConnectionAsync(c.Id, ct), ct)));
        return Result.Success<IReadOnlyList<ConnectionView>>(views);
    }
}

[RequestHandler<DeleteConnectionCommand, Guid>]
public sealed class DeleteConnectionHandler(BankingAccess access, IConnectionStore connections, IEnableBankingClient client)
    : IRequestHandler<DeleteConnectionCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteConnectionCommand command, CancellationToken ct = default)
    {
        var app = await access.AppIdAsync(ct);
        if (!app.TryGetValue(out var appId))
            return BankingAccess.Failed<Guid, string>(app);
        var mine = await access.MyConnectionAsync(command.Id, ct);
        if (!mine.TryGetValue(out var connection))
            return BankingAccess.Failed<Guid, BankConnection>(mine);

        // An expired or revoked consent has nothing left to revoke; a live one must be revoked before Nid forgets its session.
        if (connection!.Status is ConnectionStatus.Active or ConnectionStatus.Error && await connections.SessionIdAsync(connection.Id, ct) is { } session)
        {
            var revoked = await client.DeleteSessionAsync(appId!, session, ct);
            // A 404: the session is already gone there.
            if (revoked.TryGetFailure(out var failure) && failure!.Code != EnableBankingErrors.Prefix + "HTTP_404"
                && EnableBankingErrors.Classify(failure.Code) is not (ProviderErrorKind.Expired or ProviderErrorKind.Revoked))
                return Result.Failure<Guid>(failure);
        }

        await connections.DeleteAsync(connection.Id, ct);
        return Result.Success(connection.Id);
    }
}
