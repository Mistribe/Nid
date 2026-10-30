using Nid.Banking.Contracts;
using Nid.Banking.Domain.Files;
using NodaTime;

namespace Nid.Banking.Tests.Files;

public class Camt053ReaderTests
{
    private const string Iban = "FR7630006000011234567890189";

    [Fact]
    public void Reads_a_camt_053_001_02_statement()
    {
        var file = StatementReader.Read(Vectors.Read("camt053-001-02.xml"));

        Assert.Equal("camt053", file.Format);
        Assert.Equal(Iban, file.Iban);
        Assert.Equal(Iban, file.AccountId);
        Assert.Equal("EUR", file.Currency);
        Assert.Equal(1234.56m, file.ClosingBalance);
        Assert.Equal(new LocalDate(2026, 10, 28), file.ClosingBalanceOn);
        Assert.Equal(new LocalDate(2026, 10, 1), file.From);
        Assert.Equal(new LocalDate(2026, 10, 28), file.To);
        Assert.Equal(
            [
                new StatementLine("REF-20261015-0002", new LocalDate(2026, 10, 15), new LocalDate(2026, 10, 14), -84.20m,
                    "PRLV SEPA EDF ECHEANCE ELECTRICITE OCTOBRE 2026", "EDF CLIENTS PARTICULIERS", "PRLV SEPA EDF ECHEANCE ELECTRICITE OCTOBRE 2026"),
                new StatementLine("REF-20261027-0003", new LocalDate(2026, 10, 27), null, 2450m,
                    "VIR SEPA SOCIETE EXEMPLE SAS SALAIRE", "SOCIETE EXEMPLE SAS", null),
            ],
            file.Lines);
    }

    [Fact]
    public void Reads_a_camt_053_001_08_statement()
    {
        var file = StatementReader.Read(Vectors.Read("camt053-001-08.xml"));

        Assert.Equal(Iban, file.Iban);
        Assert.Equal("EUR", file.Currency);
        Assert.Equal(-56.78m, file.ClosingBalance);
        Assert.Equal(new LocalDate(2026, 10, 28), file.ClosingBalanceOn);
        Assert.Equal(
            [
                new StatementLine("REF-20261012-0001", new LocalDate(2026, 10, 12), new LocalDate(2026, 10, 11), -13.49m,
                    "CARTE X1234 11/10 AMAZON EU SARL", "AMAZON EU SARL", "CARTE X1234 11/10 AMAZON EU SARL"),
                new StatementLine("REF-20261020-0002", new LocalDate(2026, 10, 20), null, 30m,
                    "REMBOURSEMENT RESTAURANT", "MARIE EXEMPLE", "REMBOURSEMENT RESTAURANT"),
            ],
            file.Lines);
    }

    [Fact]
    public void Pending_entries_are_skipped_and_an_empty_statement_is_valid()
    {
        var file = StatementReader.Read(Vectors.Read("camt-pending.xml"));

        Assert.Empty(file.Lines);
        Assert.Null(file.Iban);
        Assert.Equal("00012345678", file.AccountId);
        Assert.Equal(5000m, file.ClosingBalance);
        Assert.Null(file.From);
    }

    [Fact]
    public void An_entity_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Vectors.Read("camt-entity.xml")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.DoesNotContain("ENTITY-EXPANDED", e.Message);
        Assert.DoesNotContain("localhost", e.Message);
    }

    [Fact]
    public void An_entry_without_an_amount_is_refused_with_its_line()
    {
        var text = System.Text.Encoding.UTF8.GetString(Vectors.Read("camt053-001-02.xml")).Replace("<Amt Ccy=\"EUR\">84.20</Amt>", "");
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.Equal(31, e.Line);
    }

    [Fact]
    public void An_amount_over_15_digits_is_refused_with_its_line()
    {
        var e = Assert.Throws<StatementFileException>(() => Read(t => t.Replace(">84.20<", ">10000000000000000.12<")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.Equal(32, e.Line);
    }

    [Fact]
    public void Nesting_deeper_than_32_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => Read(t => t.Replace(
            "<Sts>BOOK</Sts>", "<Sts>" + string.Concat(Enumerable.Repeat("<a>", 40)) + "BOOK" + string.Concat(Enumerable.Repeat("</a>", 40)) + "</Sts>")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
    }

    [Fact]
    public void Statements_of_one_account_are_read_as_one()
    {
        var file = StatementReader.Read(Statements((Iban, 20, "200.00", 1), (Iban, 10, "100.00", 1)));

        Assert.Equal(["20-0", "10-0"], file.Lines.Select(l => l.ProviderRef));
        Assert.Equal(200m, file.ClosingBalance);
        Assert.Equal(new LocalDate(2026, 10, 20), file.ClosingBalanceOn);
        Assert.Equal(new LocalDate(2026, 10, 10), file.From);
        Assert.Equal(new LocalDate(2026, 10, 20), file.To);
    }

    [Fact]
    public void Statements_of_two_accounts_are_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Statements((Iban, 20, "200.00", 1), ("FR7630004008200001234567K82", 10, "100.00", 1))));
        Assert.Equal(BankingCodes.FileSeveralAccounts, e.Code);
    }

    [Fact]
    public void The_line_cap_counts_the_whole_file()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Statements((Iban, 20, "200.00", 10_001), (Iban, 10, "100.00", 10_000))));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
    }

    [Fact]
    public void A_booked_date_past_the_latest_allowed_is_refused_with_its_line()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Vectors.Read("camt053-001-02.xml"), latest: new LocalDate(2026, 10, 20)));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.NotNull(e.Line);
    }

    [Fact]
    public void A_booking_date_of_year_0_is_refused()
        => Assert.Equal(BankingCodes.FileMalformed, Assert.Throws<StatementFileException>(() => Read(t => t.Replace("<BookgDt><Dt>2026-10-15</Dt>", "<BookgDt><Dt>0000-01-01</Dt>"))).Code);

    private static StatementFile Read(Func<string, string> edit)
        => StatementReader.Read(System.Text.Encoding.UTF8.GetBytes(edit(System.Text.Encoding.UTF8.GetString(Vectors.Read("camt053-001-02.xml")))));

    private static byte[] Statements(params (string Iban, int Day, string Closing, int Lines)[] statements)
    {
        var xml = new System.Text.StringBuilder("<?xml version=\"1.0\"?><Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.053.001.08\"><BkToCstmrStmt>");
        foreach (var (iban, day, closing, lines) in statements)
        {
            var date = $"2026-10-{day:D2}";
            xml.Append($"<Stmt><FrToDt><FrDtTm>{date}T00:00:00</FrDtTm><ToDtTm>{date}T23:59:59</ToDtTm></FrToDt>")
                .Append($"<Acct><Id><IBAN>{iban}</IBAN></Id><Ccy>EUR</Ccy></Acct>")
                .Append($"<Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy=\"EUR\">{closing}</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>{date}</Dt></Dt></Bal>");
            for (var i = 0; i < lines; i++)
                xml.Append($"<Ntry><Amt Ccy=\"EUR\">1.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts><BookgDt><Dt>{date}</Dt></BookgDt><AcctSvcrRef>{day}-{i}</AcctSvcrRef></Ntry>");
            xml.Append("</Stmt>");
        }

        return System.Text.Encoding.UTF8.GetBytes(xml.Append("</BkToCstmrStmt></Document>").ToString());
    }

    [Fact]
    public void Broken_xml_is_refused_with_its_line()
    {
        var text = System.Text.Encoding.UTF8.GetString(Vectors.Read("camt053-001-02.xml")).Replace("</AcctSvcrRef>\n        <NtryDtls>", "\n        <NtryDtls>");
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.NotNull(e.Line);
    }
}
