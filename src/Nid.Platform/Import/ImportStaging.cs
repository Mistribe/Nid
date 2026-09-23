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

public sealed record ImportSession(
    Guid Id,
    PersonId Person,
    ImportState State,
    Instant CreatedAt,
    Instant ExpiresAt,
    IReadOnlyDictionary<string, PersonId> Adults,
    string? ReportJson);

/// <summary>One accepted batch. <paramref name="Payload"/> is the RFC 8785 canonical form of its entities.</summary>
public sealed record StoredBatch(string Section, int Index, string Hash, int Count, string Payload);

/// <summary>
/// The staging of <c>import-format.md</c> §11.3. Every read of a session goes through
/// <see cref="FindOwnedAsync"/>, which filters on the person: another person's session and a missing one are
/// indistinguishable to the caller.
/// </summary>
public interface IImportStaging
{
    /// <summary>Marks every active session past its expiry as expired and deletes its batches.</summary>
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
    /// <paramref name="from"/>, and says whether it did. Two racing callers cannot both succeed.
    /// </summary>
    Task<bool> TransitionAsync(Guid session, IReadOnlyCollection<ImportState> from, ImportState to, Instant now, Instant expiresAt, string? reportJson, CancellationToken ct);

    Task ClearBatchesAsync(Guid session, CancellationToken ct);
}
