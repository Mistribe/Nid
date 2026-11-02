using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.Application.Import;

/// <summary>A source record, parsed; owners carry their kind so the Core's rules can run on it.</summary>
internal sealed record ParsedSource(IncomeKind Kind, Currency Currency, Visibility Visibility, IReadOnlyList<Owner> Owners, LocalDate? EndedOn,
    ShareLevel ShareLevel = ShareLevel.View);

/// <summary>A phase record, parsed; the label is trimmed, as the API stores it.</summary>
internal sealed record ParsedPhase(
    string Label, LocalDate StartsOn, LocalDate? EndsOn, Money NetAmount, Duration Every, Money? GrossAmount, int? MaxCycles, string? Note, Money? TaxableAmount = null);

/// <summary>
/// File records to income inputs. Every refusal names the member it concerns; an entity's issues are all collected
/// rather than stopping at the first, hence the non-short-circuit <c>&amp;</c>.
/// </summary>
internal static class IncomeParsing
{
    /// <summary>API field → file member, for <see cref="SourceRules"/>' and <see cref="PhaseRules"/>' <c>field</c> metadata.</summary>
    private static readonly Dictionary<string, string> Members = new(StringComparer.Ordinal)
    {
        ["displayName"] = "name", ["payerLabel"] = "payer_label", ["note"] = "note",
        ["label"] = "label", ["startsOn"] = "starts_on", ["endsOn"] = "ends_on", ["every"] = "every", ["maxCycles"] = "max_cycles",
        ["netAmount"] = "net_amount", ["grossAmount"] = "gross_amount", ["taxableAmount"] = "taxable_amount",
    };

    /// <summary>The members that hold an amount: a refusal names its <c>.amount</c>, or its <c>.currency</c> for a mismatch.</summary>
    private static readonly HashSet<string> Amounts = new(StringComparer.Ordinal) { "netAmount", "grossAmount", "taxableAmount" };

    private static readonly Dictionary<string, DurationUnit> Units = new(StringComparer.Ordinal)
    {
        ["day"] = DurationUnit.Day, ["week"] = DurationUnit.Week, ["month"] = DurationUnit.Month, ["year"] = DurationUnit.Year,
    };

    /// <summary>
    /// Local rules (2C1 spec §5), the API's own: <see cref="SourceRules"/> gives the codes; its API field names become the
    /// file's. <paramref name="today"/> is null on receipt (a batch alone does not ask the calendar): <c>ended_on</c> is
    /// then only parsed.
    /// </summary>
    public static bool TrySource(IncomeSourceRecord r, string path, Func<string, ResolvedPerson?> person, LocalDate? today,
        List<ImportIssue> issues, out ParsedSource parsed)
    {
        parsed = null!;
        var ok = Take(SourceRules.Kind(r.Kind), path + ".kind", issues, out var kind)
                 & Check(SourceRules.Name(r.Name), path, issues)
                 & Check(SourceRules.Check(r.PayerLabel, r.Note), path, issues);
        if (!Currency.TryCreate(r.Currency, out var currency))
            ok = Invalid(issues, path + ".currency", "A currency is an ISO 4217 code in capitals.");

        var visibility = Visibility.Shared;
        if (r.Visibility == "private")
            visibility = Visibility.Private;
        else if (r.Visibility != "shared")
            ok = Invalid(issues, path + ".visibility", "Visibility is shared or private.");
        if (ImportSectionKit.ShareLevelRefusal(r.ShareLevel, visibility, out var level) is { } refused)
            ok = Invalid(issues, path + ".share_level", refused);

        var owners = new List<Owner>();
        for (var j = 0; j < r.Ownership.Count; j++)
            if (person(r.Ownership[j].Person) is { } owner)
                owners.Add(new Owner(owner.Id, owner.Kind, r.Ownership[j].ShareBp));
            else
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{r.Ownership[j].Person}' is not in persons.",
                    $"{path}.ownership[{j}].person"));
                ok = false;
            }

        ok &= TryDate(r.EndedOn, path + ".ended_on", issues, out var endedOn);
        if (endedOn is { } e && today is { } t && e > t)
        {
            issues.Add(ImportIssue.Error(CoreFactCodes.DateInFuture, "A source cannot end in the future.", path + ".ended_on"));
            ok = false;
        }
        if (ok)
            parsed = new ParsedSource(kind, currency, visibility, owners, endedOn, level);
        return ok;
    }

    /// <summary>
    /// Local rules of a phase (<see cref="PhaseRules.Check"/>) at the file's members. <paramref name="currency"/> is the
    /// source's; null on receipt, where the net amount's stands for it (a gross amount in another currency is still refused).
    /// </summary>
    public static bool TryPhase(IncomePhaseRecord r, string path, Currency? currency, List<ImportIssue> issues, out ParsedPhase parsed)
    {
        parsed = null!;
        var ok = TryMoney(r.NetAmount, path + ".net_amount", issues, out var net)
                 & TryOptionalMoney(r.GrossAmount, path + ".gross_amount", issues, out var gross)
                 & TryOptionalMoney(r.TaxableAmount, path + ".taxable_amount", issues, out var taxable)
                 & TryEvery(r.Every, path + ".every", issues, out var every)
                 & TryDate(r.StartsOn, path + ".starts_on", issues, out var startsOn)
                 & TryDate(r.EndsOn, path + ".ends_on", issues, out var endsOn);
        if (!ok)
            return false;

        if (!Check(PhaseRules.Check(r.Label, startsOn!.Value, endsOn, net, every, gross, taxable, r.MaxCycles, r.Note, currency ?? net.Currency), path, issues))
            return false;
        parsed = new ParsedPhase(r.Label.Trim(), startsOn.Value, endsOn, net, every, gross, r.MaxCycles, r.Note, taxable);
        return true;
    }

    /// <summary>A refusal of <see cref="SourceRules"/> or <see cref="PhaseRules"/>, at the file member its field names.</summary>
    public static bool Check(Result result, string path, List<ImportIssue> issues)
    {
        if (!result.TryGetFailure(out var failure))
            return true;
        var member = "";
        if (failure!.Metadata is { } metadata && metadata.TryGetValue("field", out var field) && field is string name
            && Members.TryGetValue(name, out var m))
            member = "." + m + (Amounts.Contains(name) ? failure.Code == CoreFactCodes.CurrencyMismatch ? ".currency" : ".amount" : "");
        issues.Add(ImportIssue.Error(failure.Code, failure.Message, path + member));
        return false;
    }

    /// <summary>A file date; <c>import.invalid_value</c> when it is not one.</summary>
    private static bool TryDate(string? text, string path, List<ImportIssue> issues, out LocalDate? date)
    {
        date = null;
        if (text is null)
            return true;
        var parse = LocalDatePattern.Iso.Parse(text);
        if (!parse.Success)
            return Invalid(issues, path, "A date is written YYYY-MM-DD.");
        date = parse.Value;
        return true;
    }

    private static bool TryMoney(MoneyRecord r, string path, List<ImportIssue> issues, out Money money)
    {
        money = default;
        if (!Currency.TryCreate(r.Currency, out var currency))
            return Invalid(issues, path + ".currency", "A currency is an ISO 4217 code in capitals.");
        var created = Money.Create(r.Amount, currency);
        if (created.TryGetValue(out money))
            return true;
        created.TryGetFailure(out var failure);
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".amount"));
        return false;
    }

    private static bool TryOptionalMoney(MoneyRecord? r, string path, List<ImportIssue> issues, out Money? money)
    {
        money = null;
        if (r is null)
            return true;
        if (!TryMoney(r, path, issues, out var value))
            return false;
        money = value;
        return true;
    }

    /// <summary>A unit the Core does not know, or a count under one, is no cadence of a phase either.</summary>
    private static bool TryEvery(IncomeDurationRecord r, string path, List<ImportIssue> issues, out Duration every)
    {
        every = default;
        if (Units.TryGetValue(r.Unit, out var unit) && Duration.Create(r.Count, unit).TryGetValue(out every))
            return true;
        issues.Add(ImportIssue.Error(IncomeCodes.UnknownCadence, "A phase comes every 1 to 12 months, or every year.", path));
        return false;
    }

    private static bool Take<T>(Result<T> result, string path, List<ImportIssue> issues, out T value) where T : notnull
    {
        if (result.TryGetValue(out value!))
            return true;
        result.TryGetFailure(out var failure);
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path));
        return false;
    }

    private static bool Invalid(List<ImportIssue> issues, string path, string message)
    {
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, message, path));
        return false;
    }
}
