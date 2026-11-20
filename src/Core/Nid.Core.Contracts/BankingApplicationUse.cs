namespace Nid.Core.Contracts;

/// <summary>#382: codes Core and the banking module share.</summary>
public static class BankingApplicationCodes
{
    /// <summary>An Enable Banking application that connections still use cannot be removed or replaced.</summary>
    public const string InUse = "banking.application_in_use";
}

/// <summary>#382: lets Core ask the banking module about the installation's application id, without knowing it.</summary>
public interface IBankingApplicationUse
{
    /// <summary>
    /// Whether making <paramref name="applicationId"/> the installation's id would orphan a connection: one past its pending state made
    /// with an application that is neither this id nor its creator's own.
    /// </summary>
    Task<bool> WouldOrphanAsync(string applicationId, CancellationToken ct);
}
