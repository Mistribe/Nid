using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application.Import;

/// <summary>An envelope record, parsed; owners carry their kind so the Core's rules can run on it.</summary>
internal sealed record ParsedEnvelope(
    EnvelopeKind Kind, ValuationCadence Cadence, Currency Currency, Visibility Visibility, IReadOnlyList<Owner> Owners,
    LocalDate? OpenedOn, LocalDate? ClosedOn);

/// <summary>
/// File records to envelope inputs. Every refusal names the member it concerns; an entity's issues are all collected
/// rather than stopping at the first, hence the non-short-circuit <c>&amp;</c>.
/// </summary>
internal static class EnvelopeParsing
{
    /// <summary>API field → file member, for <see cref="EnvelopeRules"/>' <c>field</c> metadata.</summary>
    private static readonly Dictionary<string, string> Members = new(StringComparer.Ordinal)
    {
        ["displayName"] = "name", ["institution"] = "institution", ["accountHint"] = "account_hint",
        ["staleAfterDays"] = "stale_after_days", ["note"] = "note",
    };

    /// <summary>
    /// Local rules (2B1b spec §4), the API's own: <see cref="EnvelopeRules"/> gives the codes; its API field names become
    /// the file's. <paramref name="today"/> is null on receipt (a batch alone does not ask the calendar) and at apply
    /// (validation decided it on the same transaction): dates are then only parsed.
    /// </summary>
    public static bool TryEnvelope(EnvelopeRecord r, string path, Func<string, ResolvedPerson?> person, LocalDate? today,
        List<ImportIssue> issues, out ParsedEnvelope parsed)
    {
        parsed = null!;
        var ok = Take(EnvelopeRules.Kind(r.Kind), path + ".kind", issues, out var kind)
                 & Take(EnvelopeRules.Cadence(r.ValuationCadence), path + ".valuation_cadence", issues, out var cadence)
                 & Check(EnvelopeRules.Name(r.Name), path, issues)
                 & Check(EnvelopeRules.Check(r.Institution, r.AccountHint, r.StaleAfterDays, r.Note), path, issues);
        if (!Currency.TryCreate(r.Currency, out var currency))
            ok = Invalid(issues, path + ".currency", "A currency is an ISO 4217 code in capitals.");

        var visibility = Visibility.Shared;
        if (r.Visibility == "private")
            visibility = Visibility.Private;
        else if (r.Visibility != "shared")
            ok = Invalid(issues, path + ".visibility", "Visibility is shared or private.");

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

        ok &= TryDate(r.OpenedOn, path + ".opened_on", today, issues, out var openedOn)
              & TryDate(r.ClosedOn, path + ".closed_on", today, issues, out var closedOn);
        if (ok)
            parsed = new ParsedEnvelope(kind, cadence, currency, visibility, owners, openedOn, closedOn);
        return ok;
    }

    /// <summary>A file date; <c>import.invalid_value</c> when it is not one, <c>core.date_in_future</c> after <paramref name="today"/>.</summary>
    public static bool TryDate(string? text, string path, LocalDate? today, List<ImportIssue> issues, out LocalDate? date)
    {
        date = null;
        if (text is null)
            return true;
        var parse = LocalDatePattern.Iso.Parse(text);
        if (!parse.Success)
            return Invalid(issues, path, "A date is written YYYY-MM-DD.");
        if (today is { } t && parse.Value > t)
        {
            issues.Add(ImportIssue.Error(CoreFactCodes.DateInFuture, "This date is after today.", path));
            return false;
        }
        date = parse.Value;
        return true;
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
