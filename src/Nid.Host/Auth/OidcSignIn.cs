using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Auth;

/// <summary>
/// What the OIDC callback does once the identity provider has answered: provision the adult as the system, then
/// record <c>login</c> or <c>login_refused</c> (1F3a spec §5). A class of its own so tests can run it without a
/// real identity provider.
/// </summary>
public sealed class OidcSignIn(IActorAccessor accessor, IInvoker invoker, ISecurityEvents events)
{
    public async Task<Result<PersonInfo>> CompleteAsync(string subject, string name, string? email, CancellationToken ct)
    {
        accessor.Current = Actor.System;
        var result = await invoker.InvokeAsync(new ProvisionAdultCommand(subject, name, email), ct);

        if (result.TryGetValue(out var person))
            await events.RecordAsync(SecurityEvent.Of(SecurityEventNames.Login, person.Id), Actor.Browser(person.Id, person.IsAdmin), ct);
        else if (result.TryGetFailure(out var failure) && failure!.Code == PersonCodes.NotRegistered)
            // The subject only: the email and the name a claim carried are the person's, not the log's.
            await events.RecordAsync(new SecurityEvent(SecurityEventNames.LoginRefused, null, [],
                new Dictionary<string, string> { ["subject"] = subject }), Actor.System, ct);

        return result;
    }
}
