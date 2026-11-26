using Nid.Ai.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Application;

/// <summary>#450: the embeddings of the suggested patterns (<c>banking.pattern_embedding</c>), one per pattern and model.</summary>
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
}

/// <summary>
/// #450 (candidate 1, <c>docs/ai/README.md</c> §7.2), in the background: embeds the suggested patterns through the gateway's
/// <c>embed</c> use while the household has the <c>decide</c> use on. A pattern is at most two words of a label, with no digit
/// (<see cref="CategoryRules.Suggest"/>); one holding a word of a household member's name is never sent. Only vectors are kept:
/// the vote runs when the suggestions are read, over the reader's own lines.
/// </summary>
public sealed class PatternIndexer(IPatternIndexStore index, IEmbeddings embeddings, IPersonRegistry persons, IClock clock)
{
    public const int Batch = 32;
    private const int BatchesPerPass = 10;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!await embeddings.UseOnAsync(ModelUses.Decide, ct) || await embeddings.ModelAsync(ct) is not { } model)
            return;
        var heldBack = CategorySuggestions.NameWords((await persons.ListAsync(ct)).Select(p => p.DisplayName));
        var adults = (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult).Select(p => p.Id.Value).ToList();
        for (var n = 0; n < BatchesPerPass && await index.UnembeddedAsync(model, heldBack, Batch, ct) is { Count: > 0 } patterns; n++)
        {
            // Every adult's allow before a remote worker; a cloud provider only with bank_labels among the kinds allowed.
            var embedded = await embeddings.EmbedAsync(
                new LlmEmbedRequest(patterns, DataClasses.BankLabels, adults, Kinds: [DataClasses.BankLabels]), ct);
            if (!embedded.TryGetValue(out var result) || result.Model != model)
                return; // no worker, the use turned off, the model changed: the next pass tries again
            await index.SaveAsync(model, result.Dimension, [.. patterns.Zip(result.Vectors)], clock.GetCurrentInstant(), ct);
        }
    }
}
