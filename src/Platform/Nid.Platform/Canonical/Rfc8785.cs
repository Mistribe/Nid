using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nid.Platform.Canonical;

/// <summary>
/// The JSON Canonicalization Scheme of RFC 8785: members sorted by UTF-16 code units, minimal escaping, no
/// whitespace, numbers serialized as ECMAScript does. It identifies batches (<c>import-format.md</c> §11.2) and
/// entities (<c>import_hash</c>); it never computes with a value, so the <c>double</c> below is harmless —
/// amounts are read as <c>decimal</c> by the file records, not from here.
/// </summary>
public static class Rfc8785
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Canonicalize(JsonElement element)
    {
        var output = new StringBuilder();
        Write(element, output);
        return output.ToString();
    }

    public static byte[] CanonicalUtf8(JsonElement element)
    {
        var canonical = Canonicalize(element);
        try
        {
            return StrictUtf8.GetBytes(canonical);
        }
        catch (EncoderFallbackException invalid)
        {
            throw new FormatException("A string holds a lone surrogate, which RFC 8785 cannot represent.", invalid);
        }
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 canonical form.</summary>
    public static string Hash(JsonElement element) => Convert.ToHexStringLower(SHA256.HashData(CanonicalUtf8(element)));

    /// <summary>ECMAScript <c>Number.prototype.toString</c> (ECMA-262 §7.1.12.1), as RFC 8785 §3.2.2.3 requires.</summary>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
            throw new FormatException("NaN and infinities have no JSON representation.");
        if (value == 0)
            return "0"; // negative zero too

        var (digits, n) = ShortestDigits(Math.Abs(value));
        var k = digits.Length;

        string body;
        if (k <= n && n <= 21)
            body = digits + new string('0', n - k);
        else if (0 < n && n <= 21)
            body = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0)
            body = "0." + new string('0', -n) + digits;
        else
        {
            var exponent = n - 1;
            var sign = exponent < 0 ? "-" : "+";
            var mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
            body = mantissa + "e" + sign + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        }

        return value < 0 ? "-" + body : body;
    }

    /// <summary>
    /// The shortest digit string that round-trips, and <c>n</c> such that value = 0.digits × 10^n. .NET's "R"
    /// format already yields the shortest round-trip digits; only its layout differs from ECMAScript's.
    /// </summary>
    private static (string Digits, int N) ShortestDigits(double positive)
    {
        var text = positive.ToString("R", CultureInfo.InvariantCulture); // "1E+23", "0.0001", "123.45", "5E-324"
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.');
        var integerPart = point >= 0 ? text[..point] : text;
        var fraction = point >= 0 ? text[(point + 1)..] : "";

        var digits = integerPart + fraction;
        var n = integerPart.Length + exponent;

        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0')
            leading++;

        return (digits[leading..].TrimEnd('0'), n - leading);
    }

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new List<(string Name, JsonElement Value)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var name = ReadStringOrThrow(() => property.Name);
                    if (!seen.Add(name))
                        throw new FormatException($"Member '{name}' appears twice: RFC 8785 requires unique names.");
                    members.Add((name, property.Value));
                }

                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name)); // UTF-16 code units
                output.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                        output.Append(',');
                    WriteString(members[i].Name, output);
                    output.Append(':');
                    Write(members[i].Value, output);
                }
                output.Append('}');
                break;

            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                        output.Append(',');
                    first = false;
                    Write(item, output);
                }
                output.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(ReadStringOrThrow(() => element.GetString()!), output);
                break;

            case JsonValueKind.Number:
                output.Append(FormatNumber(double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)));
                break;

            case JsonValueKind.True:
                output.Append("true");
                break;

            case JsonValueKind.False:
                output.Append("false");
                break;

            case JsonValueKind.Null:
                output.Append("null");
                break;

            default:
                throw new FormatException($"Unexpected JSON value kind {element.ValueKind}.");
        }
    }

    /// <summary>
    /// Reads a JSON string or member name that may hold a <c>\uD800</c>-<c>\uDFFF</c> escape with no matching
    /// pair: <see cref="JsonElement.GetString"/> and <see cref="JsonProperty.Name"/> both reject it while
    /// unescaping, with an <see cref="InvalidOperationException"/> that has nothing to do with a caller error.
    /// RFC 8785 has no representation for a lone surrogate, so this is the one place that turns it into the
    /// <see cref="FormatException"/> every other rejection in this class uses.
    /// </summary>
    private static string ReadStringOrThrow(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException invalid)
        {
            throw new FormatException("A string holds a lone surrogate, which RFC 8785 cannot represent.", invalid);
        }
    }

    /// <summary>RFC 8785 §3.2.2.2: escape <c>"</c>, <c>\</c> and control characters; nothing else.</summary>
    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
}
