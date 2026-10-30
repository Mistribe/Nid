using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Nid.Banking.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Banking.Domain.Files;

/// <summary>Spec §5: an OFX (1.x SGML, 2.x XML) or CAMT.053 statement read in memory, refused whole when anything is wrong.</summary>
public static class StatementReader
{
    public const int MaxBytes = 5 * 1024 * 1024, MaxLines = 20_000;

    /// <summary>Real statements nest about 12 deep; XLinq recurses on depth (<c>Value</c>), so a deeper file is refused before it overflows the stack.</summary>
    internal const int MaxDepth = 32;

    internal const string NoLabel = "(sans libellé)";

    /// <summary>A year 0 or a typo stays out: it would move a read window, or overflow a date sum.</summary>
    public static readonly LocalDate Earliest = new(1900, 1, 1);

    /// <summary>How far ahead of today a booking may be (a card's deferred debit, a time zone): the latest a reader is given.</summary>
    public const int AheadDays = 7;

    private static readonly LocalDatePattern Compact = LocalDatePattern.CreateWithInvariantCulture("uuuuMMdd");

    /// <summary>
    /// Sniffs the format (OFX header <c>OFXHEADER:</c>/<c>&lt;OFX&gt;</c>/<c>&lt;?OFX</c>, or a camt.053 <c>Document</c> root) and reads it.
    /// No date is before 1900; no booking or closing balance after <paramref name="latest"/>, when given.
    /// </summary>
    /// <exception cref="StatementFileException">Too large, of an unknown format, or malformed.</exception>
    public static StatementFile Read(ReadOnlySpan<byte> file, LocalDate? latest = null)
    {
        if (file.Length > MaxBytes)
            throw new StatementFileException(BankingCodes.FileTooLarge, null, "The file is over 5 MiB.");
        if (file.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            file = file[3..];

        var head = Encoding.Latin1.GetString(file[..Math.Min(file.Length, 1024)]).TrimStart();
        if (head.StartsWith("OFXHEADER:", StringComparison.Ordinal) || head.StartsWith("<OFX>", StringComparison.Ordinal))
            return OfxReader.ReadSgml(file, latest);
        if (head.StartsWith('<') && (file.IndexOf("<?OFX"u8) >= 0 || file.IndexOf("<OFX>"u8) >= 0))
            return OfxReader.Read(LoadXml(file).Root!, latest);
        if (head.StartsWith('<') && file.IndexOf("urn:iso:std:iso:20022:tech:xsd:camt.053."u8) >= 0)
            return Camt053Reader.Read(LoadXml(file).Root!, latest);
        throw new StatementFileException(BankingCodes.FileFormat, null, "The file is neither OFX nor CAMT.053.");
    }

    /// <summary>No DTD, so no entity of any kind (XXE, expansion bombs); the depth checked by a first pass; the elements keep their line.</summary>
    private static XDocument LoadXml(ReadOnlySpan<byte> file)
    {
        var bytes = file.ToArray();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using (var scan = XmlReader.Create(new MemoryStream(bytes), settings))
                while (scan.Read())
                    if (scan.NodeType == XmlNodeType.Element && scan.Depth > MaxDepth)
                        throw Malformed(((IXmlLineInfo)scan).LineNumber, $"The file nests more than {MaxDepth} deep.");
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw Malformed(e.LineNumber > 0 ? e.LineNumber : null, "The file is not well-formed XML.");
        }
    }

    /// <summary>Statements of one account (one per day, say) read as one, the closing balance being the latest; several accounts are refused.</summary>
    internal static StatementFile Merge(IReadOnlyList<StatementFile> statements, IReadOnlyList<StatementLine> lines)
    {
        var first = statements[0];
        if (statements.Any(s => (s.Iban, s.AccountId, s.Currency) != (first.Iban, first.AccountId, first.Currency)))
            throw new StatementFileException(BankingCodes.FileSeveralAccounts, null, "The file holds statements of several accounts.");
        var closing = statements.Where(s => s.ClosingBalanceOn is not null).OrderBy(s => s.ClosingBalanceOn).LastOrDefault();
        return first with
        {
            ClosingBalance = closing?.ClosingBalance, ClosingBalanceOn = closing?.ClosingBalanceOn,
            From = statements.Min(s => s.From), To = statements.Max(s => s.To), Lines = lines,
        };
    }

    internal static StatementFileException Malformed(int? line, string message) => new(BankingCodes.FileMalformed, line, message);

    internal static StatementFileException Malformed(XElement at, string message) => Malformed(
        at.Annotation<SgmlLine>()?.Number ?? (at is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null), message);

    /// <summary>The trimmed text, <c>null</c> when absent or blank.</summary>
    internal static string? Text(XElement? e) => e?.Value.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A comma or a point as the decimal separator; what <c>numeric(19,4)</c> holds (under 10^15, 4 decimals).</summary>
    internal static decimal Amount(XElement e)
        => decimal.TryParse(e.Value.Trim().Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
           && Math.Abs(amount) < 1e15m && decimal.Round(amount, 4) == amount
            ? amount
            : throw Malformed(e, $"<{e.Name.LocalName}> is not an amount of at most 15 digits and 4 decimals.");

    /// <summary>
    /// OFX dates from their first 8 digits (<c>20261012120000[+1:CET]</c>), ISO ones from their first 10 characters; from 1900 on, and
    /// up to <paramref name="latest"/> when given (a booking: one far ahead would move every later read past the real lines).
    /// </summary>
    internal static LocalDate Date(XElement e, bool ofx, LocalDate? latest = null)
    {
        var (pattern, length) = ofx ? (Compact, 8) : (LocalDatePattern.Iso, 10);
        var text = e.Value.Trim();
        if (text.Length < length || pattern.Parse(text[..length]) is not { Success: true } result)
            throw Malformed(e, $"<{e.Name.LocalName}> is not a date.");
        return result.Value < Earliest || result.Value > latest
            ? throw Malformed(e, $"<{e.Name.LocalName}> is before 1900 or more than a week ahead.")
            : result.Value;
    }
}
