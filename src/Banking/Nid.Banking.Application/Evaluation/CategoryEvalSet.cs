using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;

namespace Nid.Banking.Application;

/// <summary>
/// #450: the evaluation set every model is calibrated on (<c>category-eval.json</c>, shipped in this assembly): invented French bank
/// labels with the seed category expected (null: none fits), and an invented history of categorised lines. Expense categories for
/// debits, income ones for credits; a transfer category is never an option.
/// </summary>
public sealed record CategoryEvalSet(int Version, IReadOnlyList<CategoryEvalSet.HistoryEntry> History, IReadOnlyList<CategoryEvalSet.Case> Cases)
{
    public sealed record HistoryEntry(string Label, bool Debit, string Category, int Lines);

    public sealed record Case(string Label, string? Counterparty, bool Debit, string? Expected);

    public static CategoryEvalSet Current { get; } = Load();

    private static CategoryEvalSet Load()
    {
        using var file = typeof(CategoryEvalSet).Assembly.GetManifestResourceStream("Nid.Banking.Application.Evaluation.category-eval.json")!;
        return JsonSerializer.Deserialize<CategoryEvalSet>(file, JsonSerializerOptions.Web)!;
    }

    /// <summary>A label's suggested pattern, as a household's line of that label gets it.</summary>
    public static string? Pattern(string label) => CategoryRules.Suggest(TransactionLabel.Normalize(label));

    public IReadOnlyList<DecideCalibration.Case> CalibrationCases()
        => [.. Cases.Select(c => new DecideCalibration.Case(Pattern(c.Label)!, c.Debit, c.Expected is { } key ? CategorySeed.Find(key)!.Id : null))];

    public IReadOnlyList<CategorySuggestions.Evidence> Evidence()
        => [.. History.Select(h => new CategorySuggestions.Evidence(Pattern(h.Label)!, h.Debit, CategorySeed.Find(h.Category)!.Id, h.Lines))];

    /// <summary>Every seed category but the transfers, on its side, as a model reads it.</summary>
    public static IReadOnlyList<DecideCalibration.Option> Options()
        => [.. CategorySeed.All.Where(c => c.Kind != CategoryKind.Transfer).Select(c => new DecideCalibration.Option(c.Id, c.Kind == CategoryKind.Expense,
            CategorySuggestions.OptionText(c.ParentKey is { } parent ? CategorySeed.Find(parent)!.Name : null, c.Name)))];
}
