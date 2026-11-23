using Microsoft.Extensions.Logging;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// #414: hands the deprecated installation application (the household's id and the server's key) to one signed-up adult as their own,
/// so their connections made with it keep syncing. Every other connection made with it ends: its creator's accounts and history stay.
/// </summary>
public sealed record TransferInstallationApplicationCommand(Guid PersonId)
    : ICommand<bool>, IAdminOnly, IBrowserOnly, ITransactional, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is true ? SecurityEvent.Of(SecurityEventNames.BankingApplicationTransferred, new PersonId(PersonId)) : null;
}

[RequestHandler<TransferInstallationApplicationCommand, bool>]
public sealed partial class TransferInstallationApplicationHandler(
    IHouseholdSettings settings, BankingOptions options, IPersonRegistry persons, IApplicationStore applications, IConnectionStore connections,
    IEnableBankingClient client, IUnitOfWork unitOfWork, ILogger<TransferInstallationApplicationHandler> log)
    : IRequestHandler<TransferInstallationApplicationCommand, bool>
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "A bank session ended by the transfer could not be deleted at Enable Banking.")]
    private partial void LogSessionNotDeleted(Exception exception);

    public async ValueTask<Result<bool>> HandleAsync(TransferInstallationApplicationCommand command, CancellationToken ct = default)
    {
        if ((await settings.ReadAsync(ct)).EnableBankingAppId is not { Length: > 0 } appId || string.IsNullOrWhiteSpace(options.PrivateKey))
            return Result.Failure<bool>(new ConflictFailure("No installation application to transfer.") { Code = BankingCodes.NotConfigured });
        var person = new PersonId(command.PersonId);
        if (!(await persons.ListAsync(ct)).Any(p => p.Id == person && p.Kind == PersonKind.Adult && !p.IsPending))
            return Result.Failure<bool>(new NotFoundFailure("person", command.PersonId.ToString()));
        if (await applications.FindAsync(person, ct) is { } own && await connections.InUseAsync(own.ApplicationId, person, ct))
            return MyApplication.InUse<bool>();

        // The others must not read on with an application that is now someone else's: their consents end.
        var sessions = new List<string>();
        foreach (var other in (await connections.UsingAsync(appId, ct)).Where(c => c.Person != person))
        {
            if (other.Status == ConnectionStatus.Pending)
            {
                await connections.DeleteAsync(other.Id, ct);
                continue;
            }
            if (other.Status is ConnectionStatus.Active or ConnectionStatus.Error && await connections.SessionIdAsync(other.Id, ct) is { } session)
                sessions.Add(session);
            await connections.SetStatusAsync(other.Id, ConnectionStatus.Revoked, BankingCodes.ApplicationTransferred, ct);
        }
        // The chosen adult's legacy connections now match their own application (AppForAsync).
        await connections.StampLegacyAsync(appId, ct);
        if (!await applications.SaveAsync(person, appId, options.PrivateKey, ct))
            return MyApplication.Changed<bool>();
        var cleared = await settings.UpdateAsync(new HouseholdSettingsChange(null, null, EnableBankingAppId: ""), ct);
        if (cleared.TryGetFailure(out var failure))
            return Result.Failure<bool>(failure!);

        var installation = new EnableBankingApp(appId, options.PrivateKey);
        unitOfWork.AfterCommit(async () =>
        {
            // Revoked at the bank once Nid has let go of them; a failure there changes nothing for Nid, the consent lapsing on its own.
            foreach (var session in sessions)
                try
                {
                    await client.DeleteSessionAsync(installation, session, CancellationToken.None);
                }
                catch (Exception e)
                {
                    LogSessionNotDeleted(e);
                }
        });
        return Result.Success(true);
    }
}
