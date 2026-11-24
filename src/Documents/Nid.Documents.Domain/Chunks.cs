using System.Text.RegularExpressions;

namespace Nid.Documents.Domain;

/// <summary>A piece of a document's text that is indexed on its own (#559): its place in the document, its page, its text.</summary>
public sealed record Chunk(int Ordinal, int Page, string Text);

/// <summary>
/// How a document's text is cut for its index (#559): each page on its own, its blanks collapsed, in pieces of at most
/// <see cref="MaxChars"/> characters (about 300 tokens) cut between words, each starting about <see cref="Overlap"/>
/// characters before the end of the one before, so a sentence across a cut is whole in one of them.
/// </summary>
public static partial class Chunks
{
    public const int MaxChars = 1200;
    public const int Overlap = 200;

    public static IReadOnlyList<Chunk> Of(IEnumerable<(int Page, string Text)> pages)
    {
        var chunks = new List<Chunk>();
        foreach (var (page, raw) in pages)
        {
            var text = Blanks().Replace(raw, " ").Trim();
            for (var start = 0; start < text.Length;)
            {
                var end = Math.Min(start + MaxChars, text.Length);
                if (end < text.Length && text.LastIndexOf(' ', end, end - start) is var space and > 0 && space > start)
                    end = space; // between words, unless one word fills the whole piece
                chunks.Add(new Chunk(chunks.Count, page, text[start..end].Trim()));
                if (end == text.Length)
                    break;

                // The next one starts at the first word of the overlap; within a word too long for a piece, exactly there.
                var next = Math.Max(end - Overlap, start + 1);
                if (text[next - 1] != ' ' && text.IndexOf(' ', next, end - next) is var after and >= 0)
                    next = after + 1;
                start = next;
            }
        }

        return chunks;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Blanks();
}
