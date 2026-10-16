using System.Text.RegularExpressions;
using Nid.Core.Domain;

namespace Nid.Core.Tests;

/// <summary>tokens-and-credentials.md §1.2: the format, the prefix and the hash (spec 4A §2).</summary>
public class TokenFormatTests
{
    [Fact]
    public void A_generated_token_has_the_documented_shape_and_its_hash()
    {
        var (token, prefix, hash) = TokenFormat.Generate();

        Assert.Matches(new Regex("^nid_pat_[a-z0-9]{8}_[A-Za-z0-9_-]{43}$"), token);
        Assert.Equal(token[8..16], prefix);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), hash);
        Assert.Equal(TokenFormat.Hash(token), hash);
        Assert.True(TokenFormat.Matches(token, hash));
    }

    [Fact]
    public void Two_tokens_differ()
        => Assert.NotEqual(TokenFormat.Generate().Token, TokenFormat.Generate().Token);

    [Fact]
    public void The_prefix_is_read_from_a_well_formed_token()
    {
        var (token, prefix, _) = TokenFormat.Generate();
        Assert.True(TokenFormat.TryReadPrefix(token, out var read));
        Assert.Equal(prefix, read);
    }

    // Review Focus 4: a password manager's trailing newline is not trimmed away.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nid_pat_abcdefgh_")]
    [InlineData("nid_pat_ABCDEFGH_0123456789012345678901234567890123456789012")]
    [InlineData("nid_wrk_abcdefgh_0123456789012345678901234567890123456789012")]
    [InlineData(" nid_pat_abcdefgh_0123456789012345678901234567890123456789012")]
    [InlineData("nid_pat_abcdefgh_0123456789012345678901234567890123456789012\n")]
    [InlineData("nid_pat_abcdefgh_0123456789012345678901234567890123456789012=")]
    public void A_token_with_whitespace_or_a_wrong_shape_has_no_prefix(string? token)
        => Assert.False(TokenFormat.TryReadPrefix(token, out _));

    [Fact]
    public void Another_secret_does_not_match_the_hash()
    {
        var (token, _, hash) = TokenFormat.Generate();
        var other = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        Assert.False(TokenFormat.Matches(other, hash));
    }
}
