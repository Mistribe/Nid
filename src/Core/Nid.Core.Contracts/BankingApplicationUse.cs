namespace Nid.Core.Contracts;

/// <summary>#382: lets Core ask whether bank connections were made with an Enable Banking application id, without knowing the banking module.</summary>
public interface IBankingApplicationUse
{
    /// <summary>Whether any person's connection past its pending state carries this application id.</summary>
    Task<bool> InUseAsync(string applicationId, CancellationToken ct);
}
