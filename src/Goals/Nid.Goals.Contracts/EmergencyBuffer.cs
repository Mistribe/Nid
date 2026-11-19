using Nid.Core.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Goals.Contracts;

/// <summary>
/// Issue #81: the cash a reader keeps aside, in the reference currency: the target of the open emergency fund they read (the largest
/// when several), else the months asked of the commitments they read, as a goal computes it; null without either.
/// <paramref name="GoalBehind"/>: an open goal they read is behind.
/// </summary>
public sealed record EmergencyBuffer(decimal? Amount, bool GoalBehind);

/// <summary>Issue #81: what Banking's idle cash signal reads of the goals, in the reader's <c>Me</c> scope.</summary>
public interface IEmergencyBuffer
{
    Task<Result<EmergencyBuffer>> ReadAsync(PersonId reader, int months, CancellationToken ct);
}
