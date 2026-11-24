using Nid.Ai.Contracts;
using Nid.Core.Contracts;

namespace Nid.Ai.Application;

/// <summary>Decision M (<c>docs/ai/README.md</c> §6.3): background work leaves the premises only with <c>allow</c> from each
/// adult it concerns; <c>ask</c> counts as no.</summary>
public static class OffsiteConsent
{
    public static async Task<bool> AllowedAsync(IAssistantPeople people, IReadOnlyList<Guid> concerned, CancellationToken ct)
    {
        foreach (var person in concerned)
            if ((await people.GetAsync(new PersonId(person), ct)).OffsiteDefault != OffsitePreferences.Allow)
                return false;
        return true;
    }
}
