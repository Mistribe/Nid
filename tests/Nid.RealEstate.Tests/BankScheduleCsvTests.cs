using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3F2 §3: a bank's table pasted as text, read as written; the rules are <see cref="BankSchedule"/>'s.</summary>
public class BankScheduleCsvTests
{
    private static readonly BankRow First = new(new LocalDate(2024, 2, 15), 1_159.92m, 583.33m, 576.59m, null, 199_423.41m);
    private static readonly BankRow Second = new(new LocalDate(2024, 3, 15), 1_159.92m, 581.65m, 578.27m, null, 198_845.14m);

    /// <summary>Review Focus 1: as a French spreadsheet copies it — non-breaking spaces, a currency sign, quotes, a trailing separator, CRLF, a blank line.</summary>
    [Fact]
    public void A_french_spreadsheets_copy_reads_whole()
    {
        var text = "Date;Échéance;Intérêts;Capital;Assurance;Capital restant dû\r\n"
                   + "15/02/2024;1\u00A0159,92\u00A0€;583,33;576,59;30,00;199\u202F423,41;\r\n"
                   + "\r\n"
                   + "\"15/03/2024\";\"1 159,92\";581,65;578,27;;\"198 845,14\"\r\n";

        var read = BankScheduleCsv.Read(text);

        Assert.Empty(read.Errors);
        Assert.Equal([new BankCsvRow(1, First with { Insurance = 30m }), new BankCsvRow(3, Second)], read.Rows);
    }

    [Fact]
    public void A_dash_reads_as_zero_and_an_empty_insurance_stays_absent()
    {
        var read = BankScheduleCsv.Read("15/02/2024;1159,92;583,33;-;\u2013;199423,41\n15/03/2024;1159,92;581,65;578,27;;198845,14");

        Assert.Empty(read.Errors);
        Assert.Equal([new BankCsvRow(0, First with { Principal = 0m, Insurance = 0m }), new BankCsvRow(1, Second)], read.Rows);
    }

    [Fact]
    public void Tabs_iso_dates_and_decimal_points_read_without_an_insurance_column()
    {
        var read = BankScheduleCsv.Read("2024-02-15\t1159.92\t583.33\t576.59\t199423.41\n2024-03-15\t1159.92\t581.65\t578.27\t198845.14");

        Assert.Empty(read.Errors);
        Assert.Equal([new BankCsvRow(0, First), new BankCsvRow(1, Second)], read.Rows);
    }

    [Fact]
    public void Commas_separate_when_decimal_commas_are_quoted()
    {
        var read = BankScheduleCsv.Read("15/02/2024,\"1159,92\",\"583,33\",\"576,59\",\"199423,41\"\n15/03/2024,1159.92,581.65,578.27,198845.14");

        Assert.Empty(read.Errors);
        Assert.Equal([new BankCsvRow(0, First), new BankCsvRow(1, Second)], read.Rows);
    }

    [Fact]
    public void Each_unreadable_cell_is_reported_at_its_line_and_column()
    {
        var read = BankScheduleCsv.Read(
            "Date;Échéance;Intérêts;Capital;CRD\n"
            + "15/02/2024;1159,92;583,33;576,59;199423,41\n"
            + "le 15 mars;1159,92;581,65;578,27;198845,14\n"
            + "15/04/2024;1.159,92;579,96;abc;198265,18\n"
            + "15/05/2024;1159,92;578,27\n");

        Assert.Equal([new BankCsvRow(1, First)], read.Rows);
        Assert.Equal([(2, "dueDate"), (3, "payment"), (3, "principal"), (4, "")], read.Errors.Select(e => (e.Line, e.Column)));
    }

    [Fact]
    public void Only_a_first_line_that_is_not_a_date_is_a_header()
    {
        var read = BankScheduleCsv.Read("15/02/2024;1159,92;583,33;576,59;199423,41\nTotal;1159,92;581,65;578,27;198845,14");

        Assert.Equal([new BankCsvRow(0, First)], read.Rows);
        Assert.Equal([(1, "dueDate")], read.Errors.Select(e => (e.Line, e.Column)));
    }
}
