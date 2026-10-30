using Nid.Banking.Contracts;
using Nid.Banking.Domain.Files;
using NodaTime;

namespace Nid.Banking.Tests.Files;

public class OfxReaderTests
{
    private static readonly IReadOnlyList<StatementLine> NoLines = [];

    [Fact]
    public void Reads_an_ofx_1_sgml_statement_in_latin_1()
    {
        var file = StatementReader.Read(Vectors.Read("ofx1-sgml.ofx"));

        Assert.Equal("ofx", file.Format);
        Assert.Equal("FR7630006000011234567890189", file.Iban);
        Assert.Equal("300060000112345678901", file.AccountId);
        Assert.Equal("EUR", file.Currency);
        Assert.Equal(1234.56m, file.ClosingBalance);
        Assert.Equal(new LocalDate(2026, 10, 28), file.ClosingBalanceOn);
        Assert.Equal(new LocalDate(2026, 10, 1), file.From);
        Assert.Equal(new LocalDate(2026, 10, 28), file.To);
        Assert.Equal(
            [
                new StatementLine("2026101200001", new LocalDate(2026, 10, 12), null, -13.49m, "CARTE X1234 11/10 AMAZON EU SARL AMAZON.FR PAYMENTS LU", null, null),
                new StatementLine("2026101500002", new LocalDate(2026, 10, 15), null, -84.20m, "PRLV SEPA EDF CLIENTS PARTICULIE Échéance électricité octobre", null, null),
                new StatementLine("2026102700003", new LocalDate(2026, 10, 27), null, 2450m, "VIR SEPA SOCIETE EXEMPLE SAS", null, null),
            ],
            file.Lines);
    }

    [Fact]
    public void Reads_the_same_account_from_an_ofx_2_xml_statement()
    {
        var sgml = StatementReader.Read(Vectors.Read("ofx1-sgml.ofx"));
        var xml = StatementReader.Read(Vectors.Read("ofx2-xml.ofx"));

        Assert.Equal(sgml with { Lines = NoLines }, xml with { Lines = NoLines });
        Assert.Equal(sgml.Lines, xml.Lines);
    }

    [Fact]
    public void An_iban_account_id_is_the_iban()
    {
        var text = System.Text.Encoding.Latin1.GetString(Vectors.Read("ofx1-sgml.ofx"))
            .Replace("<BANKID>30006\n", "").Replace("<BRANCHID>00001\n", "").Replace("<ACCTID>12345678901", "<ACCTID>FR7630006000011234567890189");
        var file = StatementReader.Read(System.Text.Encoding.Latin1.GetBytes(text));

        Assert.Equal("FR7630006000011234567890189", file.Iban);
        Assert.Equal("FR7630006000011234567890189", file.AccountId);
    }

    [Fact]
    public void An_encoding_header_of_utf_8_is_followed()
    {
        var text = System.Text.Encoding.Latin1.GetString(Vectors.Read("ofx1-sgml.ofx")).Replace("ENCODING:USASCII", "ENCODING:UTF-8");
        var file = StatementReader.Read(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.Equal("PRLV SEPA EDF CLIENTS PARTICULIE Échéance électricité octobre", file.Lines[1].Label);
    }

    [Fact]
    public void A_rib_with_letters_gives_its_french_iban()
        => Assert.Equal("FR2030004008200001234567K82", Read(t => t.Replace("<BANKID>30006", "<BANKID>30004").Replace("<BRANCHID>00001", "<BRANCHID>00820")
            .Replace("<ACCTID>12345678901", "<ACCTID>0001234567K")).Iban);

    [Fact]
    public void A_rib_of_another_shape_has_no_iban()
    {
        var file = Read(t => t.Replace("<ACCTID>12345678901", "<ACCTID>123456789"));
        Assert.Null(file.Iban);
        Assert.Equal("3000600001123456789", file.AccountId);
    }

    [Fact]
    public void Four_decimals_are_read() => Assert.Equal(-13.49m, Read(t => t.Replace("-13,49", "-13,4900")).Lines[0].Amount);

    [Fact]
    public void An_amount_with_more_than_4_decimals_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => Read(t => t.Replace("-13,49", "1,23456")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.Equal(43, e.Line);
    }

    [Theory]
    [InlineData("<9A>x")]
    [InlineData("<.A>x")]
    [InlineData("<_A>x")]
    public void A_tag_starts_with_a_letter(string tag)
    {
        var e = Assert.Throws<StatementFileException>(() => Read(t => t.Replace("<LANGUAGE>FRA", tag + "\n<LANGUAGE>FRA")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.Equal(19, e.Line);
    }

    [Fact]
    public void Nesting_deeper_than_32_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => Read(t => t.Replace(
            "<ACCTID>12345678901", string.Concat(Enumerable.Repeat("<X>\n", 40)) + "<ACCTID>12345678901" + string.Concat(Enumerable.Repeat("</X>\n", 40)))));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
    }

    [Fact]
    public void A_flat_file_of_5_mib_reads_in_linear_time()
    {
        var noise = new System.Text.StringBuilder(string.Concat(Enumerable.Repeat("<A>", 28)));
        while (noise.Length < StatementReader.MaxBytes - 4_000)
            noise.Append("<B>x</B>\n");
        noise.Append(string.Concat(Enumerable.Repeat("</A>", 28)));
        var file = System.Text.Encoding.Latin1.GetBytes(System.Text.Encoding.Latin1.GetString(Ofx(1)).Replace("<OFX>", "<OFX>" + noise));
        Assert.True(file.Length <= StatementReader.MaxBytes);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Single(StatementReader.Read(file).Lines);
        // Generous: a slow CI runner, never the minutes a quadratic scan takes.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Read took {watch.Elapsed}.");
    }

    [Fact]
    public void Two_statements_of_two_accounts_are_refused()
    {
        var text = System.Text.Encoding.Latin1.GetString(Ofx(1));
        var statement = text[text.IndexOf("<STMTTRNRS>", StringComparison.Ordinal)..(text.IndexOf("</STMTTRNRS>", StringComparison.Ordinal) + "</STMTTRNRS>".Length)];
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(System.Text.Encoding.Latin1.GetBytes(
            text.Replace(statement, statement + statement.Replace("12345678901", "10987654321")))));
        Assert.Equal(BankingCodes.FileSeveralAccounts, e.Code);
    }

    [Fact]
    public void A_malformed_amount_is_refused_with_its_line()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Vectors.Read("ofx-malformed.ofx")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.Equal(51, e.Line);
    }

    [Fact]
    public void An_unclosed_aggregate_is_refused()
    {
        var text = System.Text.Encoding.Latin1.GetString(Vectors.Read("ofx1-sgml.ofx")).Replace("</OFX>", "");
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(System.Text.Encoding.Latin1.GetBytes(text)));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
    }

    [Fact]
    public void An_entity_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Vectors.Read("ofx-entity.ofx")));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.DoesNotContain("ENTITY-EXPANDED", e.Message);
        Assert.DoesNotContain("localhost", e.Message);
    }

    [Fact]
    public void More_than_20_000_lines_are_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(Ofx(StatementReader.MaxLines + 1)));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
    }

    [Fact]
    public void Exactly_20_000_lines_are_read() => Assert.Equal(StatementReader.MaxLines, StatementReader.Read(Ofx(StatementReader.MaxLines)).Lines.Count);

    [Fact]
    public void An_empty_statement_is_valid()
    {
        var file = StatementReader.Read(Ofx(0));
        Assert.Empty(file.Lines);
        Assert.Equal("EUR", file.Currency);
    }

    [Fact]
    public void A_booked_date_past_the_latest_allowed_is_refused_with_its_line()
    {
        var text = System.Text.Encoding.Latin1.GetString(Vectors.Read("ofx1-sgml.ofx"));
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(System.Text.Encoding.Latin1.GetBytes(text), latest: new LocalDate(2026, 10, 20)));
        Assert.Equal(BankingCodes.FileMalformed, e.Code);
        Assert.Equal(text.Split('\n').ToList().FindIndex(l => l.StartsWith("<DTPOSTED>20261027", StringComparison.Ordinal)) + 1, e.Line);
        Assert.Equal(3, StatementReader.Read(System.Text.Encoding.Latin1.GetBytes(text), latest: new LocalDate(2026, 10, 28)).Lines.Count);
    }

    [Theory]
    [InlineData("<DTPOSTED>20261027", "<DTPOSTED>00000101")]
    [InlineData("<DTPOSTED>20261027", "<DTPOSTED>18991231")]
    [InlineData("<DTASOF>20261028", "<DTASOF>00000101")]
    public void A_date_before_1900_is_refused(string from, string to)
        => Assert.Equal(BankingCodes.FileMalformed, Assert.Throws<StatementFileException>(() => Read(t => t.Replace(from, to))).Code);

    [Fact]
    public void A_credit_card_statement_is_a_card_s()
    {
        var card = StatementReader.Read(Vectors.Read("ofx-card.ofx"));

        Assert.True(card.Card);
        Assert.Equal(("4970XXXXXXXX1234", null, -42.30m), (card.AccountId, card.Iban, Assert.Single(card.Lines).Amount));
        Assert.False(StatementReader.Read(Vectors.Read("ofx1-sgml.ofx")).Card);
    }

    [Theory]
    [InlineData("<NAME>VIR SEPA SOCIETE EXEMPLE SAS", "<NAME>VIR SEPA <Mme Dupont> SAS")]
    [InlineData("<NAME>VIR SEPA SOCIETE EXEMPLE SAS", "<NAME>VIR SEPA </DUPONT> SAS")]
    public void A_refused_tag_is_not_echoed(string from, string to)
    {
        var e = Assert.Throws<StatementFileException>(() => Read(t => t.Replace(from, to)));
        Assert.Equal((BankingCodes.FileMalformed, 61), (e.Code, e.Line));
        Assert.DoesNotContain("upont", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static StatementFile Read(Func<string, string> edit)
        => StatementReader.Read(System.Text.Encoding.Latin1.GetBytes(edit(System.Text.Encoding.Latin1.GetString(Vectors.Read("ofx1-sgml.ofx")))));

    private static byte[] Ofx(int lines)
    {
        var body = new System.Text.StringBuilder("OFXHEADER:100\nDATA:OFXSGML\nCHARSET:1252\n\n<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>EUR\n<BANKACCTFROM><ACCTID>12345678901\n</BANKACCTFROM><BANKTRANLIST>\n");
        for (var i = 0; i < lines; i++)
            body.Append("<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261012<TRNAMT>-1,00<FITID>").Append(i).Append("<NAME>CAFE</STMTTRN>\n");
        body.Append("</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>\n");
        return System.Text.Encoding.Latin1.GetBytes(body.ToString());
    }
}
