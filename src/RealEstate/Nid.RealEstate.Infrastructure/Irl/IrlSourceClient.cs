using UnambitiousFx.Myelin;
using Nid.RealEstate.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Infrastructure.Irl;

public sealed class IrlSourceClient(SafeHttpClient http, IrlOptions options) : IIrlSourceClient
{
    public const int MaxBytes = 3 * 1024 * 1024;

    public async Task<Result<byte[]>> FetchAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(options.SourceUrl, UriKind.Absolute, out var url))
            return Fail(IrlCodes.SourceMalformed, "The source URL is not valid.");
        try
        {
            var response = await http.GetAsync(url, MaxBytes, ct);
            return (int)response.Status is < 200 or > 299
                ? Fail(IrlCodes.SourceUnavailable, $"{url.Host} answered HTTP {(int)response.Status}.")
                : Result.Success(response.Body);
        }
        catch (OutboundRefusedException refused) when (refused.Reason == RefusalReasons.TooLarge)
        {
            return Fail(IrlCodes.SourceMalformed, $"{url.Host} sent more than {MaxBytes} bytes.");
        }
        catch (OutboundRefusedException refused)
        {
            return Fail(IrlCodes.SourceRefused, refused.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                   || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(IrlCodes.SourceUnavailable, $"{url.Host} could not be reached ({e.GetType().Name}).");
        }
    }

    private static Result<byte[]> Fail(string code, string message) => Result.Failure<byte[]>(new Failure(code, message, null));
}
