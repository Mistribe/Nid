namespace Nid.Core.Contracts;

/// <summary>Failure codes of persons and household settings (identity-and-access/administration-and-audit.md §2).</summary>
public static class PersonCodes
{
    public const string AdultCap = "core.adult_cap";
    public const string NotRegistered = "core.not_registered";
    public const string NameRequired = "core.name_required";
    public const string EmailRequired = "core.email_required";
    public const string EmailTaken = "core.email_taken";
    public const string PersonActive = "core.person_active";
    public const string LastAdmin = "core.last_admin";
    public const string NotAnAdult = "core.not_an_adult";
    public const string CapBelowCount = "core.cap_below_count";
    public const string UnknownCurrency = "core.unknown_currency";
    public const string SubjectTaken = "core.subject_taken";
    public const string SubjectInvalid = "core.subject_invalid";
}
