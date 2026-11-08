using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nid.Banking.Contracts;
using NodaTime;
using static Nid.Banking.Domain.Files.StatementReader;

namespace Nid.Banking.Domain.Files;

/// <summary>Spec §5: an ISO 20022 bank-to-customer statement, minor versions 02 to 13; only booked entries are kept.</summary>
internal static partial class Camt053Reader
{
    [GeneratedRegex(@"^urn:iso:std:iso:20022:tech:xsd:camt\.053\.001\.(0[2-9]|1[0-3])$")]
    private static partial Regex Namespace();

    public static StatementFile Read(XElement root, LocalDate? latest)
    {
        var ns = root.Name.Namespace;
        if (root.Name.LocalName != "Document" || !Namespace().IsMatch(ns.NamespaceName))
            throw new StatementFileException(BankingCodes.FileFormat, null, "The file is not a CAMT.053 statement of version 02 to 13.");
        var statements = root.Elements(ns + "BkToCstmrStmt").Elements(ns + "Stmt").ToList();
        if (statements.Count == 0)
            throw Malformed(root, "The file holds no statement.");
        var lines = new List<StatementLine>();
        return Merge(statements.Select(s => ReadStatement(s, ns, lines, latest)).ToList(), lines);
    }

    /// <summary>One statement, its booked lines added to those of the file.</summary>
    private static StatementFile ReadStatement(XElement statement, XNamespace ns, List<StatementLine> lines, LocalDate? latest)
    {
        var account = Child(statement, ns + "Acct");
        var id = Child(account, ns + "Id");
        var iban = Text(id.Element(ns + "IBAN"));
        var accountId = iban ?? Text(Child(Child(id, ns + "Othr"), ns + "Id")) ?? throw Malformed(id, "The account has no id.");
        // Acct/Ccy is optional in the schema: the amounts then say it.
        var currency = Text(account.Element(ns + "Ccy")) ?? statement.Descendants(ns + "Amt").Select(a => (string?)a.Attribute("Ccy")).FirstOrDefault()
            ?? throw Malformed(account, "The account has no currency.");

        foreach (var entry in statement.Elements(ns + "Ntry"))
        {
            // <Sts>BOOK</Sts> up to .07, <Sts><Cd>BOOK</Cd></Sts> after: the text is the same.
            if (Child(entry, ns + "Sts").Value.Trim() != "BOOK")
                continue;
            if (lines.Count == MaxLines)
                throw Malformed(entry, $"The file holds more than {MaxLines} lines.");
            var (amount, debit) = Signed(entry, ns);
            var details = entry.Elements(ns + "NtryDtls").Elements(ns + "TxDtls").ToList();
            var unstructured = details.Elements(ns + "RmtInf").Elements(ns + "Ustrd").Select(Text).OfType<string>().ToList();
            var remittance = unstructured.Count > 0 ? string.Join(' ', unstructured) : null;
            // The creditor of a debit, the debtor of a credit; a Party40Choice (.08 on) wraps the name in Pty.
            var party = details.Elements(ns + "RltdPties").Elements(ns + (debit ? "Cdtr" : "Dbtr")).FirstOrDefault();
            var counterparty = Text(party?.Element(ns + "Nm") ?? party?.Element(ns + "Pty")?.Element(ns + "Nm"));
            lines.Add(new StatementLine(
                Text(entry.Element(ns + "AcctSvcrRef")), DateOf(Child(entry, ns + "BookgDt"), ns, latest),
                entry.Element(ns + "ValDt") is { } valueDate ? DateOf(valueDate, ns) : null, amount,
                remittance ?? Text(entry.Element(ns + "AddtlNtryInf")) ?? counterparty ?? NoLabel, counterparty, remittance));
        }

        var closing = statement.Elements(ns + "Bal")
            .FirstOrDefault(b => Text(b.Element(ns + "Tp")?.Element(ns + "CdOrPrtry")?.Element(ns + "Cd")) == "CLBD");
        var period = statement.Element(ns + "FrToDt");
        return new StatementFile(
            "camt053", iban, accountId, currency,
            closing is null ? null : Signed(closing, ns).Amount,
            closing is null ? null : DateOf(Child(closing, ns + "Dt"), ns, latest),
            period?.Element(ns + "FrDtTm") is { } from ? Date(from, ofx: false) : null,
            period?.Element(ns + "ToDtTm") is { } to ? Date(to, ofx: false) : null,
            lines);
    }

    private static XElement Child(XElement parent, XName name)
        => parent.Element(name) ?? throw Malformed(parent, $"<{parent.Name.LocalName}> has no <{name.LocalName}>.");

    /// <summary>Amounts are unsigned in CAMT: <c>DBIT</c> makes them negative.</summary>
    private static (decimal Amount, bool Debit) Signed(XElement parent, XNamespace ns)
    {
        var amount = Math.Abs(Amount(Child(parent, ns + "Amt")));
        var debit = Text(Child(parent, ns + "CdtDbtInd")) == "DBIT";
        return (debit ? -amount : amount, debit);
    }

    /// <summary>A DateAndDateTimeChoice: <c>Dt</c> or <c>DtTm</c>.</summary>
    private static LocalDate DateOf(XElement choice, XNamespace ns, LocalDate? latest = null)
        => Date(choice.Element(ns + "Dt") ?? choice.Element(ns + "DtTm") ?? throw Malformed(choice, $"<{choice.Name.LocalName}> has no date."), ofx: false, latest);
}
