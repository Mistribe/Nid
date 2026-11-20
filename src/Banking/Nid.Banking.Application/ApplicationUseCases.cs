using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>#382: the actor's own Enable Banking application; a <see cref="NotFoundFailure"/> when they declared none (the route answers 204).</summary>
public sealed record GetMyApplicationQuery : IQuery<MyApplicationView>;

/// <summary>
/// Declares or changes the actor's application. <paramref name="PrivateKey"/> is required the first time; left out afterwards it keeps the
/// stored one. The id stays while the actor's connections use it.
/// </summary>
public sealed record SaveMyApplicationCommand(string ApplicationId, string? PrivateKey) : ICommand<MyApplicationView>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => new(SecurityEventNames.BankingApplicationSaved, null, [], new Dictionary<string, string> { ["key_replaced"] = PrivateKey is null ? "false" : "true" });
}

/// <summary>Removes the actor's application, and their pending connections made with it; refused while an active one uses it. Whether there was one.</summary>
public sealed record DeleteMyApplicationCommand : ICommand<bool>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is true ? SecurityEvent.Of(SecurityEventNames.BankingApplicationDeleted, null) : null;
}

/// <summary>« Tester la connexion » with the actor's own application.</summary>
public sealed record TestMyApplicationCommand(string RedirectUrl) : ICommand<BankingConnectionTestView>, IBrowserOnly;

internal static class MyApplication
{
    public const int MaxIdLength = 100;
    public const int MaxKeyLength = 16 * 1024;

    public static async Task<MyApplicationView> ViewAsync(StoredApplication stored, IConnectionStore connections, CancellationToken ct)
        => new(stored.ApplicationId, stored.KeyReadable ? "loaded" : "unreadable", stored.KeyLoadedAt.ToDateTimeOffset(),
            await connections.InUseAsync(stored.ApplicationId, stored.Person, ct));

    public static Result<T> Changed<T>() where T : notnull
        => Result.Failure<T>(new ConflictFailure("The application changed meanwhile: try again.") { Code = BankingCodes.ApplicationChanged });

    public static Result<T> InUse<T>() where T : notnull
        => Result.Failure<T>(new ConflictFailure("Bank connections still use this Enable Banking application.") { Code = BankingCodes.ApplicationInUse });
}

[RequestHandler<GetMyApplicationQuery, MyApplicationView>]
public sealed class GetMyApplicationHandler(BankingAccess access, IApplicationStore applications, IConnectionStore connections)
    : IRequestHandler<GetMyApplicationQuery, MyApplicationView>
{
    public async ValueTask<Result<MyApplicationView>> HandleAsync(GetMyApplicationQuery query, CancellationToken ct = default)
        => await applications.FindAsync(access.Person, ct) is { } stored
            ? Result.Success(await MyApplication.ViewAsync(stored, connections, ct))
            : Result.Failure<MyApplicationView>(new NotFoundFailure("application", access.Person.ToString()));
}

[RequestHandler<SaveMyApplicationCommand, MyApplicationView>]
public sealed class SaveMyApplicationHandler(BankingAccess access, IApplicationStore applications, IConnectionStore connections, IHouseholdSettings settings)
    : IRequestHandler<SaveMyApplicationCommand, MyApplicationView>
{
    public async ValueTask<Result<MyApplicationView>> HandleAsync(SaveMyApplicationCommand command, CancellationToken ct = default)
    {
        var id = command.ApplicationId.Trim();
        if (id.Length is 0 or > MyApplication.MaxIdLength)
            return Refused(BankingCodes.ApplicationIdInvalid, "The application id is 1 to 100 characters.", "applicationId");
        if (id == (await settings.ReadAsync(ct)).EnableBankingAppId)
            return Refused(BankingCodes.ApplicationIdInvalid, "This is the installation's application: use another id.", "applicationId");
        if (command.PrivateKey is { } pem && (pem.Length > MyApplication.MaxKeyLength || !BankingKey.IsRsaPrivateKey(pem)))
            return Refused(BankingCodes.KeyInvalid, "The private key must be an RSA private key in PEM.", "privateKey");

        var person = access.Person;
        var current = await applications.FindAsync(person, ct);
        if (current is null && command.PrivateKey is null)
            return Refused(BankingCodes.KeyRequired, "The private key is required.", "privateKey");
        if (current is not null && current.ApplicationId != id && await connections.InUseAsync(current.ApplicationId, person, ct))
            return MyApplication.InUse<MyApplicationView>();

        if (!await applications.SaveAsync(person, id, command.PrivateKey, ct) || await applications.FindAsync(person, ct) is not { } saved)
            return MyApplication.Changed<MyApplicationView>();
        return Result.Success(await MyApplication.ViewAsync(saved, connections, ct));
    }

    private static Result<MyApplicationView> Refused(string code, string message, string field)
        => Result.Failure<MyApplicationView>(FieldErrors.At(new Failure(code, message, null), field));
}

[RequestHandler<DeleteMyApplicationCommand, bool>]
public sealed class DeleteMyApplicationHandler(BankingAccess access, IApplicationStore applications, IConnectionStore connections)
    : IRequestHandler<DeleteMyApplicationCommand, bool>
{
    public async ValueTask<Result<bool>> HandleAsync(DeleteMyApplicationCommand command, CancellationToken ct = default)
    {
        var person = access.Person;
        if (await applications.FindAsync(person, ct) is not { } stored)
            return Result.Success(false);
        if (await connections.InUseAsync(stored.ApplicationId, person, ct))
            return MyApplication.InUse<bool>();

        // Past the in-use check, only pending connections can carry the id: their flow could not finish without it.
        foreach (var pending in (await connections.MineAsync(person, ct)).Where(c => c.Status == ConnectionStatus.Pending && c.ApplicationId == stored.ApplicationId))
            await connections.DeleteAsync(pending.Id, ct);
        return await applications.DeleteAsync(person, ct) ? Result.Success(true) : MyApplication.Changed<bool>();
    }
}

[RequestHandler<TestMyApplicationCommand, BankingConnectionTestView>]
public sealed class TestMyApplicationHandler(BankingAccess access, IApplicationStore applications, IEnableBankingClient client)
    : IRequestHandler<TestMyApplicationCommand, BankingConnectionTestView>
{
    public async ValueTask<Result<BankingConnectionTestView>> HandleAsync(TestMyApplicationCommand command, CancellationToken ct = default)
        => await applications.CredentialAsync(access.Person, ct) is { } app
            ? Result.Success(await ConnectionTest.RunAsync(client, app, command.RedirectUrl, ct))
            : Result.Failure<BankingConnectionTestView>(
                new ConflictFailure("No usable Enable Banking application of your own.") { Code = BankingCodes.NotConfigured });
}
