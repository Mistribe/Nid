using Nid.Platform.Outbound;
using Nid.Subscriptions.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.Infrastructure.Catalog;

/// <summary>
/// 1C2c design §5: one icon through <see cref="SafeHttpClient"/> — host list, private addresses refused, every redirect
/// revalidated, 512 KB — within 15 seconds, then <see cref="IconProcessor"/>. Every failure becomes a reason; only the
/// caller's own cancellation leaves as an exception.
/// </summary>
public sealed class CatalogIconSource(SafeHttpClient http, TimeSpan timeout) : ICatalogIconSource
{
    public CatalogIconSource(SafeHttpClient http)
        : this(http, TimeSpan.FromSeconds(IconLimits.FetchTimeoutSeconds))
    {
    }

    public async Task<Result<ProcessedIcon>> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Refuse(IconCodes.InvalidUrl, "The icon URL is not an absolute URL.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        OutboundResponse response;
        try
        {
            response = await http.GetAsync(uri, IconLimits.MaxBytes, budget.Token);
        }
        catch (OutboundRefusedException refused)
        {
            return Refuse(ReasonOf(refused.Reason), refused.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Refuse(IconCodes.Unreachable, $"{uri.Host} did not answer within {timeout.TotalSeconds:0.###} s.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return Refuse(IconCodes.Unreachable, $"{uri.Host} could not be reached ({e.GetType().Name}).");
        }

        if ((int)response.Status is < 200 or > 299)
            return Refuse(IconCodes.HttpStatus, $"{uri.Host} answered HTTP {(int)response.Status}.");
        return IconProcessor.Process(response.Body);
    }

    /// <summary>
    /// The four refusals the design names pass as they are; <c>user_info</c>, <c>too_many_redirects</c> and
    /// <c>redirect_without_location</c> leave nothing to fetch an icon from: <c>unreachable</c> (plan decision).
    /// </summary>
    private static string ReasonOf(string refusal) => refusal switch
    {
        "not_https" => IconCodes.NotHttps,
        "host_not_allowed" => IconCodes.HostNotAllowed,
        "address_refused" => IconCodes.AddressRefused,
        "too_large" => IconCodes.TooLarge,
        _ => IconCodes.Unreachable,
    };

    private static Result<ProcessedIcon> Refuse(string reason, string message)
        => Result.Failure<ProcessedIcon>(new Failure(reason, message, null));
}
