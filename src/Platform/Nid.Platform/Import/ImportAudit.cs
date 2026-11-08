using System.Security.Cryptography;
using System.Text;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Platform.Import;

/// <summary>The only format and version the import accepts (import-format.md §2); the audit entry records them.</summary>
public static class NidDataFormat
{
    public const string Name = "nid.data";
    public const int Version = 1;
}

/// <summary>1F3b spec §4.1: what one committed import leaves in the journal.</summary>
public sealed record ImportCommitRecord(
    Guid Session, PersonId Importer, string ContentHash, bool Replace, IReadOnlyDictionary<string, SectionCounts> Counts);

/// <summary>1F3b spec §4.2: the entry as the importer reads it on the session.</summary>
public sealed record ImportAuditView(DateTimeOffset CommittedAt, string ContentHash, string Format, int Version, bool Replace);

/// <summary>The journal's <c>import</c> category (1F3a U3): written in the commit's transaction, read by the session's owner.</summary>
public interface IImportAudit
{
    Task RecordAsync(ImportCommitRecord record, CancellationToken ct);

    Task<ImportAuditView?> FindAsync(Guid session, CancellationToken ct);
}

public static class ContentHash
{
    /// <summary>
    /// 1F3b spec §4.1: SHA-256 of the batch hashes in the commit order, each lowercase hex, joined by a newline, none at
    /// the end. It identifies what was received and carries none of it.
    /// </summary>
    public static string Of(IEnumerable<string> batchHashes)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", batchHashes.Select(h => h.ToLowerInvariant())))));
}
