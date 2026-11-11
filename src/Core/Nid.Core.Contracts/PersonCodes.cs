namespace Nid.Core.Contracts;

/// <summary>Failure codes of persons and household settings (identity-and-access/administration-and-audit.md §2).</summary>
public static class PersonCodes
{
    public const string AdultCap = "core.adult_cap";
    public const string NotInAccessGroup = "core.not_in_access_group";
    public const string NameRequired = "core.name_required";
    public const string EmailRequired = "core.email_required";
    public const string EmailTaken = "core.email_taken";
    public const string PersonActive = "core.person_active";
    public const string NotAnAdult = "core.not_an_adult";
    public const string CapBelowCount = "core.cap_below_count";
    public const string UnknownCurrency = "core.unknown_currency";
    public const string SubjectTaken = "core.subject_taken";
    public const string SubjectInvalid = "core.subject_invalid";
    public const string InvalidBirthDate = "core.invalid_birth_date";
    public const string InvalidBankRetention = "core.invalid_bank_retention";
    public const string InvalidRetirementAge = "core.invalid_retirement_age";

    /// <summary>#156: a dependent something still names (an item's owner, a childcare contract…) is not removed.</summary>
    public const string PersonInUse = "core.person_in_use";
}
