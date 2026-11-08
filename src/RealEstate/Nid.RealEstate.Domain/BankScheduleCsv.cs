using System.Globalization;
using System.Text;
using Nid.RealEstate.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.RealEstate.Domain;

/// <summary>A row read, on its line of the text (0-based, header and blank lines counted).</summary>
public sealed record BankCsvRow(int Line, BankRow Row);

/// <summary>A cell that cannot be read: its line, and its column (<c>dueDate</c> … <c>crd</c>), or "" for the whole line.</summary>
public sealed record BankCsvError(int Line, string Column, string Message);

public sealed record BankCsvReading(IReadOnlyList<BankCsvRow> Rows, IReadOnlyList<BankCsvError> Errors);

/// <summary>
/// Spec 3F2 §3: a bank's table pasted as text. Separator <c>;</c>, tab or <c>,</c>, detected on the first line; decimals with a
/// comma or a point, spaces (plain, non-breaking, narrow) and a currency sign ignored; dates <c>dd/mm/yyyy</c> or
/// <c>yyyy-mm-dd</c>; columns in a fixed order — date, payment, interest, principal, insurance (optional), remaining balance; a
/// first line whose first cell is not a date is a header; a cell may be quoted (<c>"1 159,92"</c>). Reads only: the rules are
/// <see cref="BankSchedule.Check"/>'s.
/// </summary>
public static class BankScheduleCsv
{
    private static readonly LocalDatePattern French = LocalDatePattern.CreateWithInvariantCulture("dd/MM/uuuu");

    public static BankCsvReading Read(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var rows = new List<BankCsvRow>();
        var errors = new List<BankCsvError>();
        char? separator = null;
        var first = true;
        for (var line = 0; line < lines.Length; line++)
        {
            if (string.IsNullOrWhiteSpace(lines[line]))
                continue;
            separator ??= lines[line].Contains('\t') ? '\t' : lines[line].Contains(';') ? ';' : ',';
            var cells = Split(lines[line], separator.Value);
            while (cells.Count > 1 && cells[^1].Length == 0)
                cells.RemoveAt(cells.Count - 1); // a trailing separator, as spreadsheets write one
            var date = Date(cells[0]);
            var header = first && date is null;
            first = false;
            if (header)
                continue;
            if (cells.Count is not (5 or 6))
            {
                errors.Add(new(line, "", "A line has 5 or 6 columns: date, payment, interest, principal, insurance (optional), remaining balance."));
                continue;
            }
            var before = errors.Count;
            if (date is null)
                errors.Add(new(line, "dueDate", "A date is written dd/mm/yyyy or yyyy-mm-dd."));
            var payment = Amount(cells[1], line, "payment", errors);
            var interest = Amount(cells[2], line, "interest", errors);
            var principal = Amount(cells[3], line, "principal", errors);
            var insurance = cells.Count == 6 && cells[4].Length > 0 ? Amount(cells[4], line, "insurance", errors) : null;
            var crd = Amount(cells[^1], line, "crd", errors);
            if (errors.Count == before)
                rows.Add(new(line, new BankRow(date!.Value, payment!.Value, interest!.Value, principal!.Value, insurance, crd!.Value)));
        }
        return new(rows, errors);
    }

    private static LocalDate? Date(string cell)
    {
        var parsed = French.Parse(cell);
        if (parsed.Success)
            return parsed.Value;
        parsed = LocalDatePattern.Iso.Parse(cell);
        return parsed.Success ? parsed.Value : null;
    }

    /// <summary>Spaces of every kind and a currency sign dropped, a decimal comma read as a point; thousands are spaces only.</summary>
    private static decimal? Amount(string cell, int line, string column, List<BankCsvError> errors)
    {
        var text = new string(cell.Where(c => !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.CurrencySymbol).ToArray())
            .Replace(',', '.');
        if (text is "-" or "\u2013")
            return 0m; // a spreadsheet's accounting format shows zero as a dash
        if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            return amount;
        errors.Add(new(line, column, "An amount is written like 1 159,92 or 1159.92."));
        return null;
    }

    /// <summary>One line's cells, trimmed; a quoted cell keeps its separators, and <c>""</c> inside it is a quote.</summary>
    private static List<string> Split(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                    cell.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                    quoted = false;
            }
            else if (c == '"')
                quoted = true;
            else if (c == separator)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
                cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
