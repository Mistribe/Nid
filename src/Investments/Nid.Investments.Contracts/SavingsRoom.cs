using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Investments.Contracts;

/// <summary>Issue #81: what Banking's idle cash signal reads of the regulated livrets.</summary>
public interface ISavingsRoom
{
    /// <summary>
    /// What can still be paid in, under their ceilings, to the open regulated livrets the reader reads (<c>Me</c> scope) on
    /// <paramref name="today"/>, in the reference currency; 0 without any.
    /// </summary>
    Task<decimal> RoomAsync(PersonId reader, LocalDate today, CancellationToken ct);
}
