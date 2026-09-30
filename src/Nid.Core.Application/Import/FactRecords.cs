using Nid.Core.Contracts;
using Nid.Core.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Core.Application.Import;

/// <summary><c>import-format.md</c> §5.6: a value of an item on a date.</summary>
internal sealed record ValuationRecord
{
    public required string Id { get; init; }
    public required string Item { get; init; }
    public required string AsOf { get; init; }
    public required MoneyRecord Amount { get; init; }
    public string? Note { get; init; }
}

/// <summary><c>import-format.md</c> §5.6: a flow of an item; its direction comes from <see cref="Kind"/>, never the sign.</summary>
internal sealed record FlowRecord
{
    public required string Id { get; init; }
    public required string Item { get; init; }
    public required string OccurredOn { get; init; }
    public required string Kind { get; init; }
    public required MoneyRecord Amount { get; init; }
    public string? Category { get; init; }
    public string? ExternalRef { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// A value or a flow of the file, parsed: what both sections check the same way. <see cref="Kind"/> is null for a value.
/// </summary>
internal sealed record ParsedFact(
    StagedEntity Entity, string Path, string Id, string Item, LocalDate Date, Money Amount,
    FlowKind? Kind, string? Category, string? ExternalRef, string? Note)
{
    public NewValuation ToValuation(ItemId item, ImportMark? mark)
        => new(item, Date, Amount, Note, FactSource.Import, mark);

    public NewFlow ToFlow(ItemId item, ImportMark? mark)
        => new(item, Date, Kind!.Value, Amount, Category, ExternalRef, Note, FactSource.Import, mark);
}

/// <summary>
/// File records to facts (2B1b spec §5). Every refusal names the member it concerns, and an entity's issues are all
/// collected (the non-short-circuit <c>&amp;</c>). The bounds are the Core's own (<see cref="FactRules"/>), never
/// restated here. <c>currency</c> and <c>today</c> are null on receipt: a batch alone knows neither the item nor the
/// household's calendar.
/// </summary>
internal static class FactParsing
{
    public static readonly IReadOnlyDictionary<string, FlowKind> FlowKinds = new Dictionary<string, FlowKind>(StringComparer.Ordinal)
    {
        ["contribution"] = FlowKind.Contribution, ["withdrawal"] = FlowKind.Withdrawal, ["opening"] = FlowKind.Opening,
        ["income"] = FlowKind.Income, ["expense"] = FlowKind.Expense,
    };

    public static string Wire(FlowKind kind) => FlowKinds.Single(k => k.Value == kind).Key;

    /// <summary><see cref="FactRules"/>' <c>field</c> metadata → the file member.</summary>
    private static readonly Dictionary<string, string> Members = new(StringComparer.Ordinal)
    {
        ["amount"] = "amount.amount", ["note"] = "note", ["category"] = "category", ["external_ref"] = "external_ref",
    };

    public static bool TryValuation(StagedEntity entity, ValuationRecord r, string path, Currency? currency, LocalDate? today,
        List<ImportIssue> issues, out ParsedFact fact)
    {
        fact = null!;
        var ok = TryDate(r.AsOf, path + ".as_of", today, issues, out var date)
                 & TryMoney(r.Amount, path + ".amount", currency, issues, out var amount);
        if (!ok)
            return false;
        var parsed = new ParsedFact(entity, path, r.Id, r.Item, date, amount, null, null, null, r.Note);
        if (!Check(FactRules.CheckValuation(parsed.ToValuation(ItemId.New(), null)), path, issues))
            return false;
        fact = parsed;
        return true;
    }

    public static bool TryFlow(StagedEntity entity, FlowRecord r, string path, Currency? currency, LocalDate? today,
        List<ImportIssue> issues, out ParsedFact fact)
    {
        fact = null!;
        var ok = TryDate(r.OccurredOn, path + ".occurred_on", today, issues, out var date);
        if (!FlowKinds.TryGetValue(r.Kind, out var kind))
            ok = Invalid(issues, path + ".kind", "A flow's kind is contribution, withdrawal, opening, income or expense.");
        if (TryMoney(r.Amount, path + ".amount", currency, issues, out var amount))
        {
            if (amount.Amount <= 0)
            {
                issues.Add(ImportIssue.Error(CoreFactCodes.AmountNotPositive, "A flow's amount is positive; its kind gives the direction.",
                    path + ".amount.amount"));
                ok = false;
            }
        }
        else
            ok = false;
        if (!ok)
            return false;
        var parsed = new ParsedFact(entity, path, r.Id, r.Item, date, amount, kind, r.Category, r.ExternalRef, r.Note);
        if (!Check(FactRules.CheckFlow(parsed.ToFlow(ItemId.New(), null)), path, issues))
            return false;
        fact = parsed;
        return true;
    }

    /// <summary>A file date: <c>import.invalid_value</c> when it is not one, <c>core.date_in_future</c> after <paramref name="today"/>.</summary>
    private static bool TryDate(string text, string path, LocalDate? today, List<ImportIssue> issues, out LocalDate date)
    {
        date = default;
        var parse = LocalDatePattern.Iso.Parse(text);
        if (!parse.Success)
            return Invalid(issues, path, "A date is written YYYY-MM-DD.");
        if (today is { } t && parse.Value > t)
        {
            issues.Add(ImportIssue.Error(CoreFactCodes.DateInFuture, "A value or a flow is not dated after today.", path));
            return false;
        }
        date = parse.Value;
        return true;
    }

    /// <summary>An amount in its item's currency (the ledger ruling: a mismatch is reported at <c>.amount.currency</c>).</summary>
    private static bool TryMoney(MoneyRecord r, string path, Currency? itemCurrency, List<ImportIssue> issues, out Money money)
    {
        money = default;
        if (!Currency.TryCreate(r.Currency, out var currency))
            return Invalid(issues, path + ".currency", "A currency is an ISO 4217 code in capitals.");
        if (itemCurrency is { } expected && currency != expected)
        {
            issues.Add(ImportIssue.Error(CoreFactCodes.CurrencyMismatch, $"This item's facts are in {expected.Code}.", path + ".currency"));
            return false;
        }

        var created = Money.Create(r.Amount, currency);
        if (created.TryGetValue(out money))
            return true;
        created.TryGetFailure(out var failure);
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".amount"));
        return false;
    }

    private static bool Check(Result result, string path, List<ImportIssue> issues)
    {
        if (!result.TryGetFailure(out var failure))
            return true;
        var member = failure!.Metadata is { } metadata && metadata.TryGetValue("field", out var field)
                     && field is string name && Members.TryGetValue(name, out var m)
            ? "." + m
            : "";
        issues.Add(ImportIssue.Error(failure.Code, failure.Message, path + member));
        return false;
    }

    private static bool Invalid(List<ImportIssue> issues, string path, string message)
    {
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, message, path));
        return false;
    }
}
