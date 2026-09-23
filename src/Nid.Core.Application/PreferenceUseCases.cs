using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>Changes the acting person's own preferences: there is no item, and no one else's to change.</summary>
public sealed record UpdatePreferencesCommand(PreferencesChange Change) : ICommand<Preferences>;

[RequestHandler<UpdatePreferencesCommand, Preferences>]
public sealed class UpdatePreferencesHandler(IActorAccessor actor, IPersonRegistry persons)
    : IRequestHandler<UpdatePreferencesCommand, Preferences>
{
    public async ValueTask<Result<Preferences>> HandleAsync(UpdatePreferencesCommand command, CancellationToken ct = default)
    {
        if (actor.Current?.PersonId is not { } person)
            return Result.Failure<Preferences>(new UnauthorizedFailure("Preferences belong to a signed-in person.", null));

        var current = await persons.PreferencesAsync(person, ct);
        if (current is null)
            return Result.Failure<Preferences>(new NotFoundFailure("person", person.ToString()));

        var applied = current.Apply(command.Change);
        if (!applied.TryGetValue(out var updated))
            return applied;

        await persons.SavePreferencesAsync(person, updated, ct);
        return Result.Success(updated);
    }
}
