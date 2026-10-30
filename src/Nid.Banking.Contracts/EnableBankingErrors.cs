namespace Nid.Banking.Contracts;

public enum ProviderErrorKind { Expired, Revoked, RateLimited, Other }

public static class EnableBankingErrors
{
    public const string Prefix = "enablebanking.";

    /// <summary>The one mapping from an API error name (with or without <see cref="Prefix"/>) to what the connection does about it.</summary>
    public static ProviderErrorKind Classify(string code) => (code.StartsWith(Prefix, StringComparison.Ordinal) ? code[Prefix.Length..] : code) switch
    {
        "EXPIRED_SESSION" => ProviderErrorKind.Expired,
        "REVOKED_SESSION" or "CLOSED_SESSION" or "SESSION_DOES_NOT_EXIST" => ProviderErrorKind.Revoked,
        // A bare 429 is Enable Banking's own limit, not the bank's: tomorrow all the same.
        "ASPSP_RATE_LIMIT_EXCEEDED" or "HTTP_429" => ProviderErrorKind.RateLimited,
        _ => ProviderErrorKind.Other,
    };
}
