using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Platform.Import;

public enum ImportState
{
    Open,
    Validated,
    Invalid,
    Committed,
    Cancelled,
    Expired,
}

public static class ImportStates
{
    /// <summary>A session in one of these states counts as the person's one active import (§11.5).</summary>
    public static IReadOnlyList<ImportState> Active { get; } = [ImportState.Open, ImportState.Validated, ImportState.Invalid];

    public static string Name(ImportState state) => state.ToString().ToLowerInvariant();

    public static ImportState Parse(string name) => Enum.Parse<ImportState>(name, ignoreCase: true);
}

/// <param name="UpdatedAt">
/// Strictly increases with every transition of the row, even under a frozen clock: it is the version a caller
/// hands back to <see cref="IImportStaging.TransitionAsync"/> to move the session only if nothing moved it since.
/// </param>
public sealed record ImportSession(
    Guid Id,
    PersonId Person,
    ImportState State,
    Instant CreatedAt,
    Instant ExpiresAt,
    IReadOnlyDictionary<string, PersonId> Adults,
    string? ReportJson,
    Instant UpdatedAt)
{
    /// <summary>
    /// The session as it stands at <paramref name="now"/>: an active one past its expiry is expired, whether or
    /// not a sweep (<see cref="IImportStaging.ExpireAsync"/>) has marked it so yet.
    /// </summary>
    public ImportSession AsOf(Instant now)
        => ImportStates.Active.Contains(State) && ExpiresAt < now ? this with { State = ImportState.Expired } : this;
}

/// <summary>One accepted batch. <paramref name="Payload"/> is the RFC 8785 canonical form of its entities.</summary>
public sealed record StoredBatch(string Section, int Index, string Hash, int Count, string Payload);

/// <summary>
/// The staging of <c>import-format.md</c> §11.3. Every read of a session goes through
/// <see cref="FindOwnedAsync"/>, which filters on the person: another person's session and a missing one are
/// indistinguishable to the caller.
/// </summary>
public interface IImportStaging
{
    /// <summary>
    /// Marks every active session past its expiry as expired, then deletes the batches of every session that
    /// is not active (open, validated or invalid) — not only the ones just expired here. Staging of a
    /// non-active session is never needed, so a batch that slips in concurrently while a session is ending is
    /// removed by this or the next call, rather than by making the two steps atomic.
    /// </summary>
    Task ExpireAsync(Instant now, CancellationToken ct);

    /// <summary><c>null</c> when the person already has an active session.</summary>
    Task<ImportSession?> CreateAsync(PersonId person, IReadOnlyDictionary<string, PersonId> adults, Instant now, Instant expiresAt, CancellationToken ct);

    Task<ImportSession?> FindOwnedAsync(Guid id, PersonId person, CancellationToken ct);

    Task<IReadOnlyList<ImportSession>> ListOwnedAsync(PersonId person, int limit, CancellationToken ct);

    Task<StoredBatch?> FindBatchAsync(Guid session, string section, int index, CancellationToken ct);

    Task<IReadOnlyList<StoredBatch>> BatchesAsync(Guid session, CancellationToken ct);

    Task<int> EntityCountAsync(Guid session, CancellationToken ct);

    /// <summary><c>false</c> when the slot (section, index) is already taken.</summary>
    Task<bool> AddBatchAsync(Guid session, StoredBatch batch, Instant now, CancellationToken ct);

    /// <summary>
    /// A conditional update: moves the session to <paramref name="to"/> only if it is in one of
    /// <paramref name="from"/> — and, when <paramref name="expectedUpdatedAt"/> is given, only if its
    /// <see cref="ImportSession.UpdatedAt"/> is still that one — and says whether it did. Two racing callers
    /// cannot both succeed. Inside a unit of work, the row stays locked until that unit of work ends.
    /// </summary>
    Task<bool> TransitionAsync(Guid session, IReadOnlyCollection<ImportState> from, ImportState to, Instant now, Instant expiresAt,
        string? reportJson, CancellationToken ct, Instant? expectedUpdatedAt = null);

    Task ClearBatchesAsync(Guid session, CancellationToken ct);
}
