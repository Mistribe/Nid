using System.Globalization;
using System.Text;

namespace Nid.Platform.Tests;

/// <summary>
/// The cross-implementation corpus of <c>docs/spikes.md</c> §5, rebuilt: 400 random documents plus the
/// adversarial cases, written with the variations a canonicalizer must undo (member order, whitespace,
/// optional escapes, alternative number spellings). <see cref="Random"/> with a seed is stable across .NET
/// versions, so the output is too.
/// </summary>
internal static class CorpusGenerator
{
    public const int Seed = 8785;
    public const int RandomDocuments = 400;

    private static readonly string[] Keys =
        ["a", "b", "A", "10", "2", "1", "", "é", "€", "😀", "\u2028", "\u2029", "\u007f", "\u0001",
         "key with space", "\"quoted\"", "\\", "ﬀ", "\uffff"];

    private static readonly string[] Strings =
        ["", "plain", "é", "€ 12.99", "😀", "\u2028\u2029", "\u007f", "\u0000\u001f", "\t\n\r\b\f", "\"\\/", "</script>",
         "ﬀ", "😀\uffff"];

    private static readonly double[] Numbers =
        [0, -0.0, 1, -1, 10, 100, 0.1, 0.5, 1e21, 1e20, 1e-7, 1e-6, 9007199254740992, -9007199254740992, 5e-324,
         1.7976931348623157e308, 333333333.3333332, 17.2, 179.86, 88.99, 12.99];

    /// <summary>What <c>docs/spikes.md</c> §5 calls the adversarial cases, one concern each.</summary>
    public static readonly string[] Adversarial =
    [
        """{"":""}""",
        """{"10":1,"2":2,"1":3,"a":4,"A":5}""",
        """{"\uffff":1,"\ud83d\ude00":2}""",
        """["\u0000\u0001\u001f\u007f\u2028\u2029"]""",
        """[-0, 0.0, 1.0E+2, 0.000001, 1e-7, 1e21, 1e20, 12.990]""",
    ];

    public static IReadOnlyList<string> Inputs()
    {
        var random = new Random(Seed);
        var documents = new List<string>(RandomDocuments + Adversarial.Length);
        for (var i = 0; i < RandomDocuments; i++)
        {
            var output = new StringBuilder();
            WriteValue(random, output, depth: 0, container: true);
            documents.Add(output.ToString());
        }

        documents.AddRange(Adversarial);
        return documents;
    }

    private static void WriteValue(Random random, StringBuilder output, int depth, bool container = false)
    {
        var choice = container ? random.Next(2) : depth >= 4 ? random.Next(2, 8) : random.Next(8);
        switch (choice)
        {
            case 0:
                var keys = Keys.OrderBy(_ => random.Next()).Take(random.Next(5)).ToList();
                output.Append('{');
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                        output.Append(',');
                    Whitespace(random, output);
                    WriteString(random, keys[i], output);
                    Whitespace(random, output);
                    output.Append(':');
                    Whitespace(random, output);
                    WriteValue(random, output, depth + 1);
                }
                Whitespace(random, output);
                output.Append('}');
                break;
            case 1:
                var count = random.Next(5);
                output.Append('[');
                for (var i = 0; i < count; i++)
                {
                    if (i > 0)
                        output.Append(',');
                    Whitespace(random, output);
                    WriteValue(random, output, depth + 1);
                }
                output.Append(']');
                break;
            case 2:
                WriteString(random, Strings[random.Next(Strings.Length)], output);
                break;
            case 3:
                var number = Numbers[random.Next(Numbers.Length)];
                // Both spellings parse back to the same double: "R" is shortest, "E17" carries 18 digits.
                output.Append(number.ToString(random.Next(2) == 0 ? "R" : "E17", CultureInfo.InvariantCulture));
                break;
            case 4:
                output.Append(random.NextInt64(-(1L << 53), 1L << 53).ToString(CultureInfo.InvariantCulture));
                break;
            case 5:
                output.Append("true");
                break;
            case 6:
                output.Append("false");
                break;
            default:
                output.Append("null");
                break;
        }
    }

    private static void Whitespace(Random random, StringBuilder output)
        => output.Append(random.Next(4) switch { 0 => "", 1 => " ", 2 => "\n  ", _ => "\t" });

    /// <summary>Escapes what JSON requires and, at random, what it merely allows.</summary>
    ///
    /// <remarks>
    /// A surrogate pair (e.g. an emoji) is escaped or left raw as one unit: deciding per UTF-16 code unit
    /// independently can leave one half escaped and the other raw, which is a lone surrogate in the generated
    /// text and fails <see cref="System.Text.Json.JsonDocument.Parse(string, System.Text.Json.JsonDocumentOptions)"/>
    /// before the canonicalizer ever sees it.
    /// </remarks>
    private static void WriteString(Random random, string value, StringBuilder output)
    {
        output.Append('"');
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var low = value[i + 1];
                if (random.Next(2) == 0)
                    output.Append(c).Append(low);
                else
                {
                    WriteEscape(random, c, output);
                    WriteEscape(random, low, output);
                }
                i += 2;
                continue;
            }

            if (c is '"' or '\\')
                output.Append('\\').Append(c);
            else if (c < 0x20 || (c > 0x7e && random.Next(2) == 0))
                WriteEscape(random, c, output);
            else if (c == '/' && random.Next(2) == 0)
                output.Append("\\/");
            else
                output.Append(c);
            i++;
        }
        output.Append('"');
    }

    private static void WriteEscape(Random random, char c, StringBuilder output)
        => output.Append("\\u").Append(((int)c).ToString(random.Next(2) == 0 ? "x4" : "X4", CultureInfo.InvariantCulture));
}
