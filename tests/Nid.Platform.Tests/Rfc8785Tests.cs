using System.Globalization;
using System.Text;
using System.Text.Json;
using Nid.Platform.Canonical;

namespace Nid.Platform.Tests;

public class Rfc8785Tests
{
    public static TheoryData<string> OfficialCases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Vectors.Copied("official", "input"), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(OfficialCases))]
    public void Reproduces_the_reference_implementation_byte_for_byte(string name)
    {
        using var input = JsonDocument.Parse(File.ReadAllBytes(Vectors.Copied("official", "input", name)));
        var expected = File.ReadAllBytes(Vectors.Copied("official", "output", name));

        Assert.Equal(expected, Rfc8785.CanonicalUtf8(input.RootElement));
    }

    public static TheoryData<string, string> AppendixB()
    {
        var data = new TheoryData<string, string>();
        foreach (var line in File.ReadAllLines(Vectors.Copied("numbers.txt")).Where(l => l.Length > 0))
        {
            var parts = line.Split(',');
            data.Add(parts[0], parts[1]);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AppendixB))]
    public void Serializes_numbers_as_RFC_8785_Appendix_B_says(string ieeeHex, string expected)
    {
        var value = BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(ieeeHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

        if (expected == "error")
            Assert.Throws<FormatException>(() => Rfc8785.FormatNumber(value));
        else
            Assert.Equal(expected, Rfc8785.FormatNumber(value));
    }

    [Fact]
    public void Sorts_members_by_UTF16_code_units_not_by_code_points()
    {
        // U+1F600 is D83D DE00 in UTF-16, which sorts before U+FFFF; by code point it would sort after.
        using var doc = JsonDocument.Parse("{\"\\uffff\":1,\"\\ud83d\\ude00\":2,\"10\":3,\"2\":4}");

        Assert.Equal("{\"10\":3,\"2\":4,\"😀\":2,\"\uffff\":1}", Rfc8785.Canonicalize(doc.RootElement));
    }

    [Fact]
    public void Escapes_only_what_RFC_8785_requires()
    {
        using var doc = JsonDocument.Parse("[\"\\u0001\\b\\t\\n\\f\\r\\\"\\\\\\/\\u007f\\u2028é\"]");

        Assert.Equal("[\"\\u0001\\b\\t\\n\\f\\r\\\"\\\\/\u007f\u2028é\"]", Rfc8785.Canonicalize(doc.RootElement));
    }

    [Fact]
    public void Refuses_duplicate_member_names()
    {
        using var doc = JsonDocument.Parse("{\"a\":1,\"a\":2}");

        Assert.Throws<FormatException>(() => Rfc8785.Canonicalize(doc.RootElement));
    }

    [Fact]
    public void Refuses_a_number_beyond_the_double_range()
    {
        using var doc = JsonDocument.Parse("[1e400]");

        Assert.Throws<FormatException>(() => Rfc8785.Canonicalize(doc.RootElement));
    }

    [Fact]
    public void Refuses_a_lone_surrogate()
    {
        // \ud800 with no matching low surrogate: RFC 8785 (via ECMAScript UTF-8) cannot represent it.
        using var doc = JsonDocument.Parse("[\"\\ud800\"]");

        Assert.Throws<FormatException>(() => Rfc8785.Canonicalize(doc.RootElement));
    }

    [Fact]
    public void Refuses_a_lone_surrogate_in_a_member_name()
    {
        using var doc = JsonDocument.Parse("{\"\\ud800\":1}");

        Assert.Throws<FormatException>(() => Rfc8785.Canonicalize(doc.RootElement));
    }

    [Fact]
    public void An_amount_written_as_a_number_and_as_its_canonical_form_hash_the_same()
    {
        using var written = JsonDocument.Parse("{ \"amount\" : 12.990, \"currency\": \"EUR\" }");
        using var canonical = JsonDocument.Parse("{\"amount\":12.99,\"currency\":\"EUR\"}");

        Assert.Equal(Rfc8785.Hash(canonical.RootElement), Rfc8785.Hash(written.RootElement));
        Assert.Matches("^[0-9a-f]{64}$", Rfc8785.Hash(written.RootElement));
    }

    [Fact]
    public void The_hash_is_the_SHA256_of_the_UTF8_canonical_form()
    {
        using var doc = JsonDocument.Parse("{\"b\":2,\"a\":\"é\"}");

        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("{\"a\":\"é\",\"b\":2}")));
        Assert.Equal(expected, Rfc8785.Hash(doc.RootElement));
    }
}
