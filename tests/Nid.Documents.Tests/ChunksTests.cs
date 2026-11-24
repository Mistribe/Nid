using Nid.Documents.Domain;

namespace Nid.Documents.Tests;

/// <summary>#559: a document's text in chunks of about 300 tokens, overlapping, each on one page.</summary>
public class ChunksTests
{
    [Fact]
    public void A_short_page_is_one_chunk()
    {
        var chunk = Assert.Single(Chunks.Of([(1, "Avis d'échéance de l'assurance habitation.")]));

        Assert.Equal((0, 1, "Avis d'échéance de l'assurance habitation."), (chunk.Ordinal, chunk.Page, chunk.Text));
    }

    [Fact]
    public void A_long_page_is_cut_between_words_and_each_chunk_repeats_the_end_of_the_one_before()
    {
        var words = Enumerable.Range(0, 600).Select(i => $"mot{i}").ToList();

        var chunks = Chunks.Of([(3, string.Join(' ', words))]);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= Chunks.MaxChars, $"{c.Text.Length} chars"));
        Assert.All(chunks, c => Assert.Equal(3, c.Page));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        // Between words: every piece is a whole word of the page.
        Assert.All(chunks.SelectMany(c => c.Text.Split(' ')), w => Assert.Contains(w, words));
        for (var i = 1; i < chunks.Count; i++)
        {
            var (before, chunk) = (chunks[i - 1].Text, chunks[i].Text);
            // The longest start of this chunk that ends the one before: at most the overlap, and most of it.
            var shared = Enumerable.Range(1, Math.Min(Chunks.Overlap, chunk.Length)).LastOrDefault(k => before.EndsWith(chunk[..k], StringComparison.Ordinal));
            Assert.InRange(shared, Chunks.Overlap / 2, Chunks.Overlap);
        }
        // Nothing lost: the last word is in the last chunk, the first in the first.
        Assert.StartsWith("mot0 ", chunks[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("mot599", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_are_never_mixed_and_empty_pages_give_nothing()
    {
        var chunks = Chunks.Of([(1, "Conditions générales"), (2, "   "), (3, "Page trois")]);

        Assert.Equal([(0, 1), (1, 3)], chunks.Select(c => (c.Ordinal, c.Page)));
    }

    [Fact]
    public void A_word_longer_than_a_chunk_is_cut_rather_than_lost()
    {
        var chunks = Chunks.Of([(1, new string('x', Chunks.MaxChars * 2 + 10))]);

        Assert.All(chunks, c => Assert.True(c.Text.Length <= Chunks.MaxChars));
        Assert.Equal(Chunks.MaxChars * 2 + 10, chunks.Sum(c => c.Text.Length) - Chunks.Overlap * (chunks.Count - 1));
    }
}
