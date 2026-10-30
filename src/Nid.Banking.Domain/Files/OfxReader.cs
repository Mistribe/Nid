using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NodaTime;
using static Nid.Banking.Domain.Files.StatementReader;

namespace Nid.Banking.Domain.Files;

/// <summary>The line of an element read from SGML, where XLinq keeps none.</summary>
internal sealed record SgmlLine(int Number);

/// <summary>Spec §5: OFX 1.x and 2.x read the same way once the SGML is a tree.</summary>
internal static partial class OfxReader
{
    [GeneratedRegex(@"^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$")]
    private static partial Regex IbanPattern();

    /// <summary>OFX 1.x: <c>KEY:VALUE</c> headers, then SGML whose leaf tags are usually left open.</summary>
    public static StatementFile ReadSgml(ReadOnlySpan<byte> file, LocalDate? latest)
    {
        // ponytail: CHARSET:1252 is read as Latin-1 (no code pages package): €, œ and ’ (0x80–0x9F) come out as control characters.
        var text = Encoding.Latin1.GetString(file);
        var body = Math.Max(text.IndexOf('<'), 0);
        if (text[..body].Split('\n').Any(h => h.Trim().Equals("ENCODING:UTF-8", StringComparison.OrdinalIgnoreCase)))
            text = Encoding.UTF8.GetString(file);
        return Read(Parse(text), latest);
    }

    /// <summary>A leaf is a tag followed by text, closed or not; an aggregate is a tag without text, closed by its end tag.</summary>
    private static XElement Parse(string text)
    {
        var root = new XElement("sgml");
        var open = new Stack<XElement>([root]);
        XElement? leaf = null;
        var line = 1;
        var at = 0;
        for (var start = text.IndexOf('<'); start >= 0;)
        {
            line += text.AsSpan(at, start - at).Count('\n');
            at = start;
            var end = text.IndexOf('>', start);
            var next = text.IndexOf('<', start + 1);
            if (end < 0 || (next >= 0 && next < end))
                throw Malformed(line, "A tag is not closed.");
            var tag = text[(start + 1)..end];
            var name = tag.TrimStart('/');
            if (name is not [>= 'A' and <= 'Z', ..] || !name.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_'))
                throw Malformed(line, "A tag is not an OFX tag."); // never the tag: it may be a label's text
            var value = text[(end + 1)..(next < 0 ? text.Length : next)].Trim();

            if (tag.StartsWith('/'))
            {
                // The leaf just read first: a closed leaf must not scan the open aggregates.
                if (leaf?.Name.LocalName != name)
                {
                    if (!open.Any(e => e.Name.LocalName == name))
                        throw Malformed(line, "A closing tag closes no open tag.");
                    while (open.Pop().Name.LocalName != name) { }
                }
                leaf = null;
            }
            else
            {
                if (open.Count - 1 > MaxDepth)
                    throw Malformed(line, $"The file nests more than {MaxDepth} deep.");
                var element = new XElement(name);
                element.AddAnnotation(new SgmlLine(line));
                open.Peek().Add(element);
                if (value.Length > 0)
                {
                    element.Value = WebUtility.HtmlDecode(value);
                    leaf = element;
                }
                else
                {
                    open.Push(element);
                    leaf = null;
                }
            }

            start = next;
        }

        if (open.Count > 1)
            throw Malformed(line, "A tag is never closed.");
        return root;
    }

    /// <summary>Fields are looked up anywhere below their aggregate: an empty SGML leaf left open swallows its next siblings.</summary>
    public static StatementFile Read(XElement root, LocalDate? latest)
    {
        var statements = root.Descendants().Where(e => e.Name.LocalName is "STMTRS" or "CCSTMTRS").ToList();
        if (statements.Count == 0)
            throw Malformed(root, "The file holds no bank statement.");
        var lines = new List<StatementLine>();
        return Merge(statements.Select(s => ReadStatement(s, lines, latest)).ToList(), lines);
    }

    /// <summary>One statement, its lines added to those of the file; a credit card's (<c>CCSTMTRS</c>) is a card's.</summary>
    private static StatementFile ReadStatement(XElement statement, List<StatementLine> lines, LocalDate? latest)
    {
        var accountNumber = Text(Field(statement, "ACCTID"))!;
        var bank = Text(First(statement, "BANKID"));
        var branch = Text(First(statement, "BRANCHID"));
        var given = IbanPattern().IsMatch(accountNumber) ? accountNumber : null;
        var iban = given ?? FrenchIban(bank, branch, accountNumber);
        var accountId = given ?? string.Concat(bank, branch, accountNumber);

        foreach (var entry in statement.Descendants("STMTTRN"))
        {
            if (lines.Count == MaxLines)
                throw Malformed(entry, $"The file holds more than {MaxLines} lines.");
            var label = string.Join(' ', new[] { Text(First(entry, "NAME")), Text(First(entry, "MEMO")) }.OfType<string>());
            lines.Add(new StatementLine(
                Text(First(entry, "FITID")), Date(Field(entry, "DTPOSTED"), ofx: true, latest), null, Amount(Field(entry, "TRNAMT")),
                label.Length > 0 ? label : NoLabel, null, null));
        }

        var balance = First(statement, "LEDGERBAL");
        var list = First(statement, "BANKTRANLIST");
        return new StatementFile(
            "ofx", iban, accountId, Text(Field(statement, "CURDEF"))!,
            balance is null ? null : Amount(Field(balance, "BALAMT")),
            balance is null ? null : Date(Field(balance, "DTASOF"), ofx: true, latest),
            First(list, "DTSTART") is { } from ? Date(from, ofx: true) : null,
            First(list, "DTEND") is { } to ? Date(to, ofx: true) : null,
            lines, statement.Name.LocalName == "CCSTMTRS");
    }

    /// <summary>
    /// A French RIB (bank 5 digits, branch 5, account 11) as its IBAN, so that an export matches the account Enable Banking gave:
    /// RIB key 97 − (89·bank + 15·branch + 3·account) mod 97, account letters as digits; ISO 13616 check digits.
    /// </summary>
    internal static string? FrenchIban(string? bank, string? branch, string account)
    {
        if (bank is not { Length: 5 } || branch is not { Length: 5 } || account.Length != 11
            || !(bank + branch).All(char.IsAsciiDigit) || !account.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)))
            return null;
        // A–I and J–R are 1–9, S–Z 2–9.
        var digits = string.Concat(account.Select(c => char.IsAsciiDigit(c) ? c : (char)('0' + (c <= 'I' ? c - 'A' + 1 : c <= 'R' ? c - 'J' + 1 : c - 'S' + 2))));
        var key = 97 - (89 * long.Parse(bank, CultureInfo.InvariantCulture) + 15 * long.Parse(branch, CultureInfo.InvariantCulture)
            + 3 * long.Parse(digits, CultureInfo.InvariantCulture)) % 97;
        var bban = string.Create(CultureInfo.InvariantCulture, $"{bank}{branch}{account}{key:D2}");
        return string.Create(CultureInfo.InvariantCulture, $"FR{98 - Mod97(bban + "FR00"):D2}{bban}");
    }

    /// <summary>ISO 7064 mod 97 with letters as 10–35.</summary>
    private static int Mod97(string text)
        => text.Aggregate(0, (rest, c) => char.IsAsciiDigit(c) ? (rest * 10 + c - '0') % 97 : (rest * 100 + c - 'A' + 10) % 97);

    private static XElement? First(XElement? parent, string name) => parent?.Descendants(name).FirstOrDefault();

    /// <summary>A field that must be there with a value.</summary>
    private static XElement Field(XElement parent, string name)
        => First(parent, name) is { } field && Text(field) is not null ? field : throw Malformed(parent, $"<{parent.Name.LocalName}> has no <{name}>.");
}
