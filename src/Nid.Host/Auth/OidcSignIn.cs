using Microsoft.Extensions.Options;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Auth;

/// <summary>
/// What the OIDC callback does once the identity provider has answered: require the access group, provision the adult as
/// the system with the admin role the provider's groups give, then record <c>login</c> (and <c>admin_granted</c> or
/// <c>admin_revoked</c> when the role changed) or <c>login_refused</c> (1F3a spec §5). A class of its own so tests can
/// run it without a real identity provider.
/// </summary>
public sealed class OidcSignIn(
    IActorAccessor accessor, IInvoker invoker, ISecurityEvents events, IPersonRegistry persons, IOptions<NidAuthOptions> options)
{
    /// <summary>The failures that mean « not this person », as opposed to a sign-in that broke.</summary>
    public static bool IsRefusal(Failure? failure)
        => failure?.Code is PersonCodes.NotInAccessGroup or PersonCodes.AdultCap or PersonCodes.EmailTaken;

    public async Task<Result<PersonInfo>> CompleteAsync(
        string subject, string name, string? email, IReadOnlyCollection<string> groups, CancellationToken ct)
    {
        var auth = options.Value;
        if (!groups.Contains(auth.AccessGroup))
            return await RefuseAsync(subject, new Failure(PersonCodes.NotInAccessGroup, "The identity provider did not put this person in the access group.", null), ct);

        accessor.Current = Actor.System;
        var wasAdmin = (await persons.FindBySubjectAsync(subject, ct))?.IsAdmin ?? false;
        var result = await invoker.InvokeAsync(new ProvisionAdultCommand(subject, name, email, groups.Contains(auth.AdminGroup)), ct);

        if (result.TryGetValue(out var person))
        {
            await events.RecordAsync(SecurityEvent.Of(SecurityEventNames.Login, person.Id), Actor.Browser(person.Id, person.IsAdmin), ct);
            if (person.IsAdmin != wasAdmin)
                await events.RecordAsync(
                    new SecurityEvent(person.IsAdmin ? SecurityEventNames.AdminGranted : SecurityEventNames.AdminRevoked, person.Id, [],
                        new Dictionary<string, string> { ["source"] = "identity_provider" }),
                    Actor.System, ct);
        }
        else if (result.TryGetFailure(out var failure) && IsRefusal(failure))
            await RecordRefusalAsync(subject, failure!, ct);

        return result;
    }

    private async Task<Result<PersonInfo>> RefuseAsync(string subject, Failure failure, CancellationToken ct)
    {
        await RecordRefusalAsync(subject, failure, ct);
        return Result.Failure<PersonInfo>(failure);
    }

    // The subject and the reason only: the email and the name a claim carried are the person's, not the log's.
    private Task<bool> RecordRefusalAsync(string subject, Failure failure, CancellationToken ct)
        => events.RecordAsync(new SecurityEvent(SecurityEventNames.LoginRefused, null, [],
            new Dictionary<string, string> { ["subject"] = subject, ["reason"] = failure.Code }), Actor.System, ct);
}
