using System.Globalization;
using System.IO.Compression;
using System.Text;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Domain.Fx;

/// <summary>
/// <c>eurofxref-hist.zip</c>: one CSV, <c>Date,USD,JPY,…,</c>, newest row first, <c>N/A</c> for a missing value,
/// each row ending with an empty column. Base EUR, rebased on USD row by row (design §4.3). A date listed twice
/// keeps the rates of its first row: one write never carries the same (date, quote) twice, which PostgreSQL's
/// <c>on conflict do update</c> refuses.
/// </summary>
public static class EcbReader
{
    public const long DefaultMaxUncompressedBytes = 50L * 1024 * 1024;

    /// <summary>A UTF-8 BOM, written by some producers (including .NET's default <see cref="StreamWriter"/>) ahead of the header.</summary>
    private const char Bom = '﻿';

    public static Result<FxReading> Read(byte[] zip, long maxUncompressedBytes = DefaultMaxUncompressedBytes)
    {
        string csv;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            if (archive.Entries.Count != 1)
                return Malformed($"Expected one file in the archive, found {archive.Entries.Count}.");

            var entry = archive.Entries[0];
            if (entry.Length > maxUncompressedBytes)
                return Malformed("The archive holds more than the decompressed bound.");

            // The declared length can lie: count what is actually decompressed.
            using var stream = entry.Open();
            using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bounded.Length + read > maxUncompressedBytes)
                    return Malformed("The archive holds more than the decompressed bound.");
                bounded.Write(buffer, 0, read);
            }

            csv = Encoding.UTF8.GetString(bounded.GetBuffer(), 0, (int)bounded.Length).TrimStart(Bom);
        }
        catch (InvalidDataException)
        {
            return Malformed("The file is not a zip archive.");
        }

        var lines = csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return Malformed("The file is empty.");

        var header = lines[0].Split(',');
        if (header[0] != "Date")
            return Malformed("The first column is not Date.");

        var columns = new Currency?[header.Length];
        for (var i = 1; i < header.Length; i++)
            columns[i] = Currency.TryCreate(header[i], out var currency) ? currency : null;

        var rates = new List<FxRate>();
        var seen = new HashSet<(LocalDate, Currency)>();
        var rejected = new List<FxRejection>();
        for (var line = 1; line < lines.Length; line++)
        {
            var cells = lines[line].Split(',');
            var parsedDate = LocalDatePattern.Iso.Parse(cells[0]);
            if (!parsedDate.Success)
                return Malformed($"Line {line + 1} does not start with a date.");
            var date = parsedDate.Value;

            var perEur = new Dictionary<Currency, decimal>();
            for (var i = 1; i < cells.Length && i < columns.Length; i++)
            {
                if (columns[i] is not { } quote || cells[i] is "" or "N/A")
                    continue;
                if (decimal.TryParse(cells[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0m)
                    perEur[quote] = value;
                else
                    rejected.Add(new FxRejection(date, quote.Code, 0m, null, FxCodes.InvalidValue));
            }

            if (!perEur.ContainsKey(Currency.Usd))
                continue; // no USD that day: nothing can be rebased

            var reading = FxRebase.ToUsd(date, Currency.Eur, perEur, FxRateSource.Ecb);
            rates.AddRange(reading.Rates.Where(r => seen.Add((r.OnDate, r.Quote))));
            rejected.AddRange(reading.Rejected);
        }

        return Result.Success(new FxReading(rates, rejected));
    }

    private static Result<FxReading> Malformed(string message)
        => Result.Failure<FxReading>(new Failure(FxCodes.SourceMalformed, message, null));
}
