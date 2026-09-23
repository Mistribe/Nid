using System.Globalization;
using System.Net;
using Nid.Core.Contracts;
using Nid.Platform.Outbound;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Infrastructure.Fx;

public sealed class FxSourceClient(SafeHttpClient http, FxOptions options) : IFxSourceClient
{
    public const int DayMaxBytes = 1024 * 1024;
    public const int EcbMaxBytes = 5 * 1024 * 1024;

    public Task<Result<FxFetch>> SubTrackerDayAsync(LocalDate date, CancellationToken ct)
        => FetchAsync(new Uri(new Uri(options.SubTrackerBaseUrl),
            date.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture) + ".json"), DayMaxBytes, ct);

    public Task<Result<FxFetch>> EcbHistoryAsync(CancellationToken ct)
        => FetchAsync(new Uri(options.EcbHistoryUrl), EcbMaxBytes, ct);

    private async Task<Result<FxFetch>> FetchAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        try
        {
            var response = await http.GetAsync(url, maxBytes, ct);
            if (response.Status == HttpStatusCode.NotFound)
                return Result.Success(new FxFetch(null));
            if ((int)response.Status is < 200 or > 299)
                return Fail(FxCodes.SourceUnavailable, $"{url.Host} answered HTTP {(int)response.Status}.");
            return Result.Success(new FxFetch(response.Body));
        }
        catch (OutboundRefusedException refused) when (refused.Reason == "too_large")
        {
            return Fail(FxCodes.SourceMalformed, $"{url.Host} sent more than {maxBytes} bytes.");
        }
        catch (OutboundRefusedException refused)
        {
            return Fail(FxCodes.SourceRefused, refused.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                   || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(FxCodes.SourceUnavailable, $"{url.Host} could not be reached ({e.GetType().Name}).");
        }
    }

    private static Result<FxFetch> Fail(string code, string message) => Result.Failure<FxFetch>(new Failure(code, message, null));
}
