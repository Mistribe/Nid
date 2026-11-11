using UnambitiousFx.Myelin;

namespace Nid.Platform;

/// <summary>
/// Nid's settings for its single outbound client (<c>security/application.md</c> §3, D21), Myelin's <see cref="SafeHttpClient"/>.
/// Myelin allows any public host when the list is empty; Nid always keeps an explicit one.
/// </summary>
public static class NidOutbound
{
    /// <summary>
    /// The catalog and the ECB (1C2a), and the hosts of the catalog's icons (1C2c design §5), the two price sources
    /// (holding prices spec §2), INSEE (rental spec §3), Discord webhooks (notifications design §5.3), Enable Banking
    /// (bank sync design): anything else is refused <c>host_not_allowed</c> until the operator adds its host
    /// (<c>Outbound:AllowedHosts</c>, which replaces this list).
    /// </summary>
    public static readonly string[] AllowedHosts =
        ["raw.githubusercontent.com", "www.ecb.europa.eu", "1000logos.net", "upload.wikimedia.org", "www.nounou-top.fr", "www.boursorama.com", "query2.finance.yahoo.com", "bdm.insee.fr", "discord.com", "discordapp.com", "api.enablebanking.com"];

    /// <summary>Sent on every request: <c>upload.wikimedia.org</c> refuses a request without one (403).</summary>
    public const string UserAgent = "Nid/1.0 (+https://github.com/Mistribe/Nid)";

    /// <summary>Nid's defaults on top of Myelin's (30 s, 3 redirects).</summary>
    public static MyelinOptions Options() => new() { AllowedHosts = [.. AllowedHosts], UserAgent = UserAgent };

    /// <summary>Applies Nid's defaults to the options Myelin registers.</summary>
    public static void Apply(MyelinOptions options)
    {
        options.AllowedHosts = [.. AllowedHosts];
        options.UserAgent = UserAgent;
    }
}
