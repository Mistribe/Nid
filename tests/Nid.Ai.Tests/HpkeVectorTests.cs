using System.Text.Json;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Tests;

/// <summary>RFC 9180 Appendix A, base mode, DHKEM(P-256): the HPKE the protocol stands on, replayed on every build so an
/// API or behaviour change of the experimental .NET type breaks CI, not production.</summary>
public sealed class HpkeVectorTests
{
    public static TheoryData<int> Entries()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Load().GetArrayLength(); i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Base_mode_P256_vectors_open_and_export(int index)
    {
        var vector = Load()[index];
        int kdf = vector.GetProperty("kdf_id").GetInt32(), aead = vector.GetProperty("aead_id").GetInt32();
        if (!HpkeSuite256.Defines(kdf, aead))
            Assert.Skip($"AEAD {aead} has no .NET equivalent (export-only).");

        var encryptions = vector.GetProperty("encryptions").EnumerateArray().ToArray();
        var exports = vector.GetProperty("exports").EnumerateArray().ToArray();

        var replay = HpkeSuite256.Replay(kdf, aead, Hex(vector, "ikmR"), Hex(vector, "enc"), Hex(vector, "info"),
            [.. encryptions.Select(e => (Hex(e, "ct"), Hex(e, "aad")))],
            [.. exports.Select(x => (Hex(x, "exporter_context"), x.GetProperty("L").GetInt32()))]);

        Assert.Equal(Hex(vector, "pkRm"), replay.PublicKey);
        Assert.Equal(encryptions.Select(e => Hex(e, "pt")), replay.Plaintexts);
        Assert.Equal(exports.Select(x => Hex(x, "exported_value")), replay.Exports);
    }

    [Fact]
    public void The_protocol_suite_is_in_the_vectors_and_supported_here()
    {
        Assert.Contains(Load().EnumerateArray(),
            v => v.GetProperty("kdf_id").GetInt32() == 1 && v.GetProperty("aead_id").GetInt32() == 1);
        Assert.True(HpkeSuite256.IsSupported);
    }

    private static JsonElement Load()
        => JsonDocument.Parse(File.ReadAllText(Vectors.Copied("rfc9180", "p256-base.json"))).RootElement;

    private static byte[] Hex(JsonElement element, string name) => Convert.FromHexString(element.GetProperty(name).GetString()!);
}
