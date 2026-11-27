using Nid.Ai.Contracts;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Application;

/// <summary>#450: the embeddings of the suggested patterns (<c>banking.pattern_embedding</c>), one per pattern and model, and of the
/// categories' option texts (<c>banking.category_embedding</c>), one per category and model.</summary>
public interface IPatternIndexStore
{
    /// <summary>Up to <paramref name="limit"/> suggested patterns of lines neither ignored nor internal transfers, with no embedding of
    /// <paramref name="model"/> and containing none of <paramref name="heldBack"/> as a whole word.</summary>
    Task<IReadOnlyList<string>> UnembeddedAsync(string model, IReadOnlyCollection<string> heldBack, int limit, CancellationToken ct);

    Task SaveAsync(string model, int dimension, IReadOnlyList<(string Pattern, IReadOnlyList<float> Vector)> vectors, Instant at, CancellationToken ct);

    /// <summary>For each of <paramref name="patterns"/>, its <paramref name="take"/> nearest among <paramref name="evidence"/> (itself
    /// left out), embedded by <paramref name="model"/>.</summary>
    Task<IReadOnlyList<CategorySuggestions.Neighbour>> NeighboursAsync(string model, IReadOnlyCollection<string> patterns, IReadOnlyCollection<string> evidence,
        int take, CancellationToken ct);

    /// <summary>Drops <paramref name="model"/>'s vectors of categories no longer in <paramref name="current"/> or whose text changed (a
    /// rename), and gives the texts of <paramref name="current"/> with no vector of <paramref name="model"/>.</summary>
    Task<IReadOnlyList<(Guid Category, string Text)>> UnembeddedCategoriesAsync(string model, IReadOnlyList<(Guid Category, string Text)> current,
        CancellationToken ct);

    Task SaveCategoriesAsync(string model, int dimension, IReadOnlyList<(Guid Category, string Text, IReadOnlyList<float> Vector)> vectors, Instant at,
        CancellationToken ct);

    /// <summary>For each of <paramref name="patterns"/>, the two categories nearest by name among <paramref name="debit"/> (for its debits)
    /// and among <paramref name="credit"/> (for its credits), embedded by <paramref name="model"/>.</summary>
    Task<IReadOnlyList<CategorySuggestions.CategoryNeighbour>> NearestCategoriesAsync(string model, IReadOnlyCollection<string> patterns,
        IReadOnlyCollection<Guid> debit, IReadOnlyCollection<Guid> credit, CancellationToken ct);
}

/// <summary>
/// #450 (candidate 1, <c>docs/ai/README.md</c> §7.2), in the background: embeds the suggested patterns, and the categories' option texts
/// for the cold start, through the gateway's <c>embed</c> use while the household has the <c>decide</c> use on. A pattern is at most two
/// words of a label, with no digit (<see cref="CategoryRules.Suggest"/>); a pattern or a category name holding a word of a household
/// member's name is never sent. Only vectors are kept: the vote runs when the suggestions are read, over the reader's own lines.
/// </summary>
public sealed class PatternIndexer(IPatternIndexStore index, IEmbeddings embeddings, IPersonRegistry persons, ICategoryStore categories, IClock clock)
{
    public const int Batch = 32;
    private const int BatchesPerPass = 10;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!await embeddings.UseOnAsync(ModelUses.Decide, ct) || await embeddings.ModelAsync(ct) is not { } model)
            return;
        var heldBack = CategorySuggestions.NameWords((await persons.ListAsync(ct)).Select(p => p.DisplayName));
        var adults = (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult).Select(p => p.Id.Value).ToList();
        // Every adult's allow before a remote worker; a cloud provider only with bank_labels among the kinds allowed.
        LlmEmbedRequest Request(IReadOnlyList<string> texts) => new(texts, DataClasses.BankLabels, adults, Kinds: [DataClasses.BankLabels]);

        var all = await categories.AllAsync(ct);
        var names = all.ToDictionary(c => c.Id, c => c.Name);
        var texts = all.Where(c => c.ArchivedAt is null && c.Kind != CategoryKind.Transfer)
            .Select(c => (c.Id, Text: CategorySuggestions.OptionText(c.Parent is { } parent ? names[parent] : null, c.Name)))
            .Where(c => !heldBack.Any(w => $" {TransactionLabel.Normalize(c.Text)} ".Contains($" {w} ", StringComparison.Ordinal))).ToList();
        foreach (var batch in (await index.UnembeddedCategoriesAsync(model, texts, ct)).Chunk(Batch))
        {
            var embedded = await embeddings.EmbedAsync(Request([.. batch.Select(b => b.Text)]), ct);
            if (!embedded.TryGetValue(out var result) || result.Model != model)
                return; // no worker, the use turned off, the model changed: the next pass tries again
            await index.SaveCategoriesAsync(model, result.Dimension, [.. batch.Zip(result.Vectors, (b, v) => (b.Category, b.Text, v))],
                clock.GetCurrentInstant(), ct);
        }

        for (var n = 0; n < BatchesPerPass && await index.UnembeddedAsync(model, heldBack, Batch, ct) is { Count: > 0 } patterns; n++)
        {
            var embedded = await embeddings.EmbedAsync(Request(patterns), ct);
            if (!embedded.TryGetValue(out var result) || result.Model != model)
                return;
            await index.SaveAsync(model, result.Dimension, [.. patterns.Zip(result.Vectors)], clock.GetCurrentInstant(), ct);
        }
    }
}
