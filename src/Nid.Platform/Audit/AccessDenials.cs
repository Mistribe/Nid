using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Platform.Audit;

/// <summary>Spec U12, throttled per (actor, item, minute); a write that never landed gives its slot back (Important #1).</summary>
public sealed class AccessDenials(ISecurityEvents events, AccessDeniedThrottle throttle) : IAccessDenials
{
    public async Task RecordAsync(PersonId person, ItemId? item, string access, CancellationToken ct)
    {
        var key = item?.Value ?? Guid.Empty;
        if (!throttle.ShouldRecord(person.Value, key))
            return;
        var recorded = await events.RecordAsync(new SecurityEvent(
            SecurityEventNames.AccessDenied, person, item is { } id ? [id] : [],
            new Dictionary<string, string> { ["access"] = access }), ct);
        if (!recorded)
            throttle.Forget(person.Value, key);
    }
}
