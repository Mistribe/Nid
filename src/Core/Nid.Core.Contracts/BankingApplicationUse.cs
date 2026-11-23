namespace Nid.Core.Contracts;

/// <summary>#382: codes Core and the banking module share.</summary>
public static class BankingApplicationCodes
{
    /// <summary>An Enable Banking application that connections still use cannot be removed or replaced.</summary>
    public const string InUse = "banking.application_in_use";

    /// <summary>#414: the installation-wide application can no longer be set, nor replaced by another id; only kept or cleared.</summary>
    public const string InstallationDeprecated = "banking.installation_application_deprecated";
}
