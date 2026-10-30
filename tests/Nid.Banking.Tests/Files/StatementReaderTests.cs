using Nid.Banking.Contracts;
using Nid.Banking.Domain.Files;

namespace Nid.Banking.Tests.Files;

public class StatementReaderTests
{
    [Theory]
    [InlineData("Date;Libellé;Montant\n12/10/2026;CB AMAZON;-13,49\n")]
    [InlineData("")]
    [InlineData("<html><body>Relevé</body></html>")]
    [InlineData("<?xml version=\"1.0\"?><Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.02\"/>")]
    [InlineData("<?xml version=\"1.0\"?><Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.053.001.14\"/>")]
    public void An_unknown_format_is_refused(string text)
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(BankingCodes.FileFormat, e.Code);
    }

    [Fact]
    public void A_file_over_5_mib_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Read(new byte[StatementReader.MaxBytes + 1]));
        Assert.Equal(BankingCodes.FileTooLarge, e.Code);
    }

    /// <summary>Real camt.053 exports open with a UTF-8 BOM: skipped before the format is sniffed, whatever the format.</summary>
    [Theory]
    [InlineData("camt053-001-02.xml", "camt053")]
    [InlineData("camt053-001-08.xml", "camt053")]
    [InlineData("ofx2-xml.ofx", "ofx")]
    [InlineData("ofx1-sgml.ofx", "ofx")]
    public void A_utf_8_bom_is_skipped(string vector, string format)
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var read = StatementReader.Read([.. bom, .. Vectors.Read(vector)]);
        Assert.Equal((format, StatementReader.Read(Vectors.Read(vector)).Lines.Count), (read.Format, read.Lines.Count));
    }
}
