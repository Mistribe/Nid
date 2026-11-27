using Nid.Ai.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>#533: each adult's assistant (§6.2, §6.3): off until they turn it on, and their preference for their data off the
/// premises (<c>ask</c> until they choose).</summary>
public interface IAssistantPeople
{
    Task<(bool Enabled, string OffsiteDefault)> GetAsync(PersonId person, CancellationToken ct);

    Task SetEnabledAsync(PersonId person, bool enabled, Instant at, CancellationToken ct);

    Task SetOffsiteDefaultAsync(PersonId person, string preference, Instant at, CancellationToken ct);

    /// <summary>The adults who chose <c>never</c>: what they own is held back off the premises.</summary>
    Task<IReadOnlySet<PersonId>> NeverAsync(CancellationToken ct);
}

public static class AssistantPersonCodes
{
    /// <summary>The person has not turned the assistant on for themselves.</summary>
    public const string Off = "ai.assistant_off";

    public const string InvalidPreference = "ai.invalid_offsite_preference";
}

public sealed record GetAssistantMeQuery : IQuery<AssistantMeView>;

/// <summary>An adult turns the assistant on or off for themselves, in the browser. Turning it on towards the cloud tells every
/// adult (§6.2).</summary>
public sealed record SetAssistantEnabledCommand(bool Enabled) : ICommand<PolicyChange<AssistantMeView>>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is PolicyChange<AssistantMeView> { Changed: true }
            ? new(SecurityEventNames.AiAssistantChanged, null, [], new Dictionary<string, string> { ["enabled"] = Enabled ? "true" : "false" })
            : null;
}

/// <summary>An adult's preference for their own data off the premises (§6.3), in the browser.</summary>
public sealed record SetAssistantOffsiteCommand(string Preference) : ICommand<PolicyChange<AssistantMeView>>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is PolicyChange<AssistantMeView> { Changed: true }
            ? new(SecurityEventNames.AiOffsiteDefaultChanged, null, [], new Dictionary<string, string> { ["preference"] = Preference })
            : null;
}

internal static class AssistantMeViews
{
    public static async Task<AssistantMeView> ReadAsync(IAssistantPeople people, IAiSettings settings, PersonId person, CancellationToken ct)
    {
        var (enabled, offsite) = await people.GetAsync(person, ct);
        return new AssistantMeView(enabled, offsite, UseModes.ReachesCloud((await settings.UseAsync(ModelUses.Chat, ct)).Mode));
    }
}

[RequestHandler<GetAssistantMeQuery, AssistantMeView>]
public sealed class GetAssistantMeHandler(IAssistantPeople people, IAiSettings settings, IActorAccessor actors) : IRequestHandler<GetAssistantMeQuery, AssistantMeView>
{
    public async ValueTask<Result<AssistantMeView>> HandleAsync(GetAssistantMeQuery query, CancellationToken ct = default)
        => Result.Success(await AssistantMeViews.ReadAsync(people, settings, actors.Current!.PersonId!.Value, ct));
}

[RequestHandler<SetAssistantEnabledCommand, PolicyChange<AssistantMeView>>]
public sealed class SetAssistantEnabledHandler(IAssistantPeople people, IAiSettings settings, IActorAccessor actors, INotifier notifier, IClock clock)
    : IRequestHandler<SetAssistantEnabledCommand, PolicyChange<AssistantMeView>>
{
    public async ValueTask<Result<PolicyChange<AssistantMeView>>> HandleAsync(SetAssistantEnabledCommand command, CancellationToken ct = default)
    {
        var person = actors.Current!.PersonId!.Value;
        var before = await AssistantMeViews.ReadAsync(people, settings, person, ct);
        if (before.Enabled == command.Enabled)
            return Result.Success(new PolicyChange<AssistantMeView>(before, Changed: false));
        var now = clock.GetCurrentInstant();
        await people.SetEnabledAsync(person, command.Enabled, now, ct);
        if (command.Enabled && before.CloudPossible)
            await DataPolicyNotices.TellAsync(notifier, "assistant_cloud_on", null, now, ct);
        return Result.Success(new PolicyChange<AssistantMeView>(await AssistantMeViews.ReadAsync(people, settings, person, ct), Changed: true));
    }
}

[RequestHandler<SetAssistantOffsiteCommand, PolicyChange<AssistantMeView>>]
public sealed class SetAssistantOffsiteHandler(IAssistantPeople people, IAiSettings settings, IActorAccessor actors, IClock clock)
    : IRequestHandler<SetAssistantOffsiteCommand, PolicyChange<AssistantMeView>>
{
    public async ValueTask<Result<PolicyChange<AssistantMeView>>> HandleAsync(SetAssistantOffsiteCommand command, CancellationToken ct = default)
    {
        if (!OffsitePreferences.All.Contains(command.Preference))
            return Result.Failure<PolicyChange<AssistantMeView>>(new Failure(AssistantPersonCodes.InvalidPreference, "ask, allow or never.", null));
        var person = actors.Current!.PersonId!.Value;
        var before = await AssistantMeViews.ReadAsync(people, settings, person, ct);
        if (before.OffsiteDefault == command.Preference)
            return Result.Success(new PolicyChange<AssistantMeView>(before, Changed: false));
        await people.SetOffsiteDefaultAsync(person, command.Preference, clock.GetCurrentInstant(), ct);
        return Result.Success(new PolicyChange<AssistantMeView>(await AssistantMeViews.ReadAsync(people, settings, person, ct), Changed: true));
    }
}
