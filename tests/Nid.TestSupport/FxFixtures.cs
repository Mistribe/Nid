using System.Globalization;
using System.IO.Compression;
using System.Text;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.TestSupport;

/// <summary>Source files as the real sources write them (checked 2026-09-23), and a source that serves them.</summary>
public static class FxFixtures
{
    public static byte[] SubTrackerDay(params (string Code, decimal Rate)[] rates)
        => Encoding.UTF8.GetBytes("{\"from\":\"USD\",\"to\":{\"USD\":1,"
            + string.Join(',', rates.Select(r => $"\"{r.Code}\":{r.Rate.ToString(CultureInfo.InvariantCulture)}")) + "}}");

    /// <summary>Newest row first, trailing empty column, like <c>eurofxref-hist.csv</c>.</summary>
    public static byte[] EcbZip(params (LocalDate Date, decimal UsdPerEur, decimal GbpPerEur)[] rows)
    {
        var csv = new StringBuilder("Date,USD,GBP,\n");
        foreach (var row in rows.OrderByDescending(r => r.Date))
            csv.Append(CultureInfo.InvariantCulture, $"{LocalDatePattern.Iso.Format(row.Date)},{row.UsdPerEur},{row.GbpPerEur},\n");

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("eurofxref-hist.csv").Open(), Encoding.UTF8);
            writer.Write(csv.ToString());
        }
        return buffer.ToArray();
    }
}

/// <summary>
/// Serves <see cref="Days"/> and <see cref="Ecb"/>; any other day is a 404, and no <see cref="Ecb"/> is the ECB's
/// 404, which the real client reports as unavailable. Never touches the network.
/// </summary>
public sealed class FakeFxSources : IFxSourceClient
{
    public Dictionary<LocalDate, byte[]> Days { get; } = [];

    public byte[]? Ecb { get; set; }

    /// <summary>When set, every request fails with this code.</summary>
    public string? FailWith { get; set; }

    public List<string> Requested { get; } = [];

    /// <summary>What every test host uses unless a test installs its own: every request is unavailable.</summary>
    public static FakeFxSources Offline() => new() { FailWith = FxCodes.SourceUnavailable };

    public Task<Result<FxFetch>> SubTrackerDayAsync(LocalDate date, CancellationToken ct)
    {
        lock (Requested)
            Requested.Add(LocalDatePattern.Iso.Format(date));
        return Task.FromResult(Serve(Days.GetValueOrDefault(date)));
    }

    public Task<Result<FxFetch>> EcbHistoryAsync(CancellationToken ct)
    {
        lock (Requested)
            Requested.Add("ecb");
        return Task.FromResult(Ecb is null && FailWith is null
            ? Result.Failure<FxFetch>(new Failure(FxCodes.SourceUnavailable, "Fake ECB: no history file.", null))
            : Serve(Ecb));
    }

    private Result<FxFetch> Serve(byte[]? bytes)
        => FailWith is { } code
            ? Result.Failure<FxFetch>(new Failure(code, "Fake source failure.", null))
            : Result.Success(new FxFetch(bytes));
}
