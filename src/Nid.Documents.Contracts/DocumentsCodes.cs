namespace Nid.Documents.Contracts;

public static class DocumentsCodes
{
    public const string TooLarge = "documents.too_large";
    public const string UnsupportedType = "documents.unsupported_type";
    public const string UnknownTargetKind = "documents.unknown_target_kind";
    public const string TargetNotFound = "documents.target_not_found";
    public const string InvalidRole = "documents.invalid_role";
    public const string InvalidField = "documents.invalid_field";
    public const string ContentMissing = "documents.content_missing";
    public const string AlreadyLinked = "documents.already_linked";
    public const string SourceRefTaken = "documents.source_ref_taken";
    public const string Duplicate = "documents.duplicate";
}

public static class DocumentRoles
{
    public const string Lease = "lease";
    public const string LoanOffer = "loan_offer";
    public const string Deed = "deed";
    public const string TaxNotice = "tax_notice";
    public const string Statement = "statement";
    public const string Payslip = "payslip";
    public const string Insurance = "insurance";
    public const string Invoice = "invoice";
    public const string AmortizationTable = "amortization_table";
    public const string CoproCall = "copro_call";
    public const string CoproMinutes = "copro_minutes";
    public const string CoproAccounts = "copro_accounts";
    public const string Contract = "contract";
    public const string TransactionNotice = "transaction_notice";
    public const string Request = "request";
    public const string Advice = "advice";
    public const string ProductSheet = "product_sheet";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Lease, LoanOffer, Deed, TaxNotice, Statement, Payslip, Insurance, Invoice, AmortizationTable, CoproCall, CoproMinutes, CoproAccounts,
        Contract, TransactionNotice, Request, Advice, ProductSheet, Other,
    };
}
