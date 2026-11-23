using Nid.Banking.Contracts;
using Nid.Banking.Domain.Files;
using UnambitiousFx.Functional;

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
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Parse(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(BankingCodes.FileFormat, e.Code);
    }

    [Fact]
    public void A_file_over_5_mib_is_refused()
    {
        var e = Assert.Throws<StatementFileException>(() => StatementReader.Parse(new byte[StatementReader.MaxBytes + 1]));
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
        var read = StatementReader.Parse([.. bom, .. Vectors.Read(vector)]);
        Assert.Equal((format, StatementReader.Parse(Vectors.Read(vector)).Lines.Count), (read.Format, read.Lines.Count));
    }

    /// <summary>#520: the readers' refusal comes out of <see cref="StatementReader.Read"/> as a failure, its line in the metadata.</summary>
    [Fact]
    public void Read_answers_a_refused_file_as_a_failure_with_its_line()
    {
        Assert.True(StatementReader.Read(Vectors.Read("ofx-malformed.ofx")).TryGetFailure(out var failure));
        Assert.Equal((BankingCodes.FileMalformed, (object?)51), (failure!.Code, failure.Metadata["line"]));
        Assert.True(StatementReader.Read(new byte[1]).TryGetFailure(out var unknown));
        Assert.False(unknown!.Metadata.ContainsKey("line"));
    }

    [Fact]
    public void Read_answers_a_good_file_as_its_statement()
    {
        Assert.True(StatementReader.Read(Vectors.Read("ofx1-sgml.ofx")).TryGetValue(out var file));
        Assert.Equal(StatementReader.Parse(Vectors.Read("ofx1-sgml.ofx")).Lines, file!.Lines);
    }
}
