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

    /// <summary>Every seed category but the transfers and the parents, on its side, as a model reads it. #821: a parent is no answer, a line
    /// goes in its most precise category; offered, it drew the picks of its children's lines, every one counted wrong.</summary>
    public static IReadOnlyList<DecideCalibration.Option> Options()
        => [.. CategorySeed.All.Where(c => c.Kind != CategoryKind.Transfer && !CategorySeed.All.Any(child => child.ParentKey == c.Key))
            .Select(c => new DecideCalibration.Option(c.Id, c.Kind == CategoryKind.Expense,
                CategorySuggestions.OptionText(c.ParentKey is { } parent ? CategorySeed.Find(parent)!.Name : null, c.Name)))];

    /// <summary>#821: the seed categories of <paramref name="debit"/>'s side as a worker is asked them, in two steps.</summary>
    public static IReadOnlyList<CategoryQuestion.Top> Tree(bool debit)
        => CategoryQuestion.Tree([.. CategorySeed.All.Where(c => c.Kind == (debit ? CategoryKind.Expense : CategoryKind.Income))
            .Select(c => (c.Id, c.ParentKey is { } parent ? CategorySeed.Find(parent)!.Id : (Guid?)null, c.Name))])!;

    /// <summary>#821: a worker's guess with what the case expects at each step: the expected category's top, then the category itself.</summary>
    public static DecideCalibration.Guess Expect(DecideCalibration.Guess guess, Guid? expected)
    {
        var top = expected is { } id && CategorySeed.All.First(c => c.Id == id).ParentKey is { } parent ? CategorySeed.Find(parent)!.Id : expected;
        return guess with { Expected = top, Then = guess.Then is { } then ? then with { Expected = expected } : null };
    }
}
