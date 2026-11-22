using Nid.Ai.Protocol.Keys;

namespace Nid.Ai.Tests;

public sealed class KeyFingerprintTests
{
    // 0x04 then 0x01..0x40: a fixed 65-byte "point"; the fingerprint only hashes bytes.
    private static readonly byte[] Key = [0x04, .. Enumerable.Range(1, 64).Select(i => (byte)i)];

    [Fact]
    public void A_fingerprint_is_sixteen_groups_of_four()
    {
        var groups = KeyFingerprint.Of(Key).Split(' ');

        Assert.Equal(16, groups.Length);
        Assert.All(groups, g => Assert.Matches("^[0-9a-f]{4}$", g));
    }

    [Fact]
    public void It_is_stable()
    {
        Assert.Equal("0ed3 a6ab 957f f6f5 9a96 30a4 73d3 1a7d 04fc b465 48b8 9016 1969 80c7 e965 84f6", KeyFingerprint.Of(Key));
        Assert.Equal("0ed3a6ab957ff6f59a9630a473d31a7d04fcb46548b89016196980c7e96584f6", KeyFingerprint.Compact(Key));
    }
}
