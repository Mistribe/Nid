using System.Text;
using Nid.Host.Auth;
using NodaTime;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>Issue 215, local-accounts.md §10: the TOTP codes the apps compute, and the recovery codes' form.</summary>
public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    // RFC 6238 appendix B, SHA-1: the last six digits of its eight.
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void The_codes_are_those_of_rfc_6238(long seconds, string code)
        => Assert.Equal(code, Totp.Code(RfcSecret, Totp.StepAt(Instant.FromUnixTimeSeconds(seconds))));

    [Fact]
    public void A_code_of_the_step_before_or_after_matches_and_no_further()
    {
        var now = Instant.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 1), now));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step + 2), now));
        Assert.Null(Totp.Match(RfcSecret, "12345", now));
        Assert.Equal(step, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step).Insert(3, " "), now));
    }

    [Fact]
    public void The_secret_is_written_in_base32_and_the_uri_names_nid_and_the_identifier()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcSecret));
        Assert.Equal("otpauth://totp/Nid:alice?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Nid&algorithm=SHA1&digits=6&period=30",
            Totp.Uri(RfcSecret, "alice"));
    }

    [Fact]
    public void Ten_recovery_codes_of_sixteen_characters_typed_loosely_are_read_the_same()
    {
        var codes = RecoveryCodes.New();

        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$", c));
        Assert.Equal(RecoveryCodes.Normalize("ABCD-0123-EFGH-1JKM"), RecoveryCodes.Normalize(" abcd o123 efgh ljkm "));
        Assert.Null(RecoveryCodes.Normalize("123456"));
        Assert.Null(RecoveryCodes.Normalize("ABCD-0123-EFGH-1JKU"));
    }
}
