using Nid.Banking.Contracts;

namespace Nid.Banking.Tests;

public class EnableBankingErrorsTests
{
    [Theory]
    [InlineData("enablebanking.EXPIRED_SESSION", ProviderErrorKind.Expired)]
    [InlineData("enablebanking.REVOKED_SESSION", ProviderErrorKind.Revoked)]
    [InlineData("enablebanking.CLOSED_SESSION", ProviderErrorKind.Revoked)]
    [InlineData("enablebanking.ASPSP_RATE_LIMIT_EXCEEDED", ProviderErrorKind.RateLimited)]
    [InlineData("enablebanking.HTTP_429", ProviderErrorKind.RateLimited)]
    [InlineData("enablebanking.SESSION_DOES_NOT_EXIST", ProviderErrorKind.Revoked)]
    [InlineData("enablebanking.ASPSP_ERROR", ProviderErrorKind.Other)]
    [InlineData("banking.provider_error", ProviderErrorKind.Other)]
    public void Each_api_error_maps_to_one_kind(string code, ProviderErrorKind kind)
        => Assert.Equal(kind, EnableBankingErrors.Classify(code));
}
