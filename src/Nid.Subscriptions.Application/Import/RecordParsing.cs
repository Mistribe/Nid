using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Application.Import;

/// <summary>A subscription record, parsed; owners carry their kind so the Core's rules can run on it.</summary>
internal sealed record ParsedSubscription(Visibility Visibility, IReadOnlyList<Owner> Owners, IReadOnlyList<PersonId> Users, LocalDate? CancelledAt);

/// <summary>
/// File records to engine inputs. Every refusal names the member it concerns. Methods collect every issue of
/// an entity rather than stopping at the first, hence the non-short-circuit <c>&amp;</c>.
/// </summary>
internal static class RecordParsing
{
    private static readonly Dictionary<string, DurationUnit> Units = new(StringComparer.Ordinal)
    {
        ["day"] = DurationUnit.Day, ["week"] = DurationUnit.Week, ["month"] = DurationUnit.Month, ["year"] = DurationUnit.Year,
    };

    private static readonly Dictionary<string, PurchaseKind> Kinds = new(StringComparer.Ordinal)
    {
        ["prepaid_term"] = PurchaseKind.PrepaidTerm, ["bundle"] = PurchaseKind.Bundle, ["gift"] = PurchaseKind.Gift,
        ["adjustment"] = PurchaseKind.Adjustment, ["refund"] = PurchaseKind.Refund,
    };

    private static readonly Dictionary<string, CoverageSource> Sources = new(StringComparer.Ordinal)
    {
        ["paid"] = CoverageSource.Paid, ["bonus"] = CoverageSource.Bonus, ["included"] = CoverageSource.Included,
        ["trial"] = CoverageSource.Trial, ["gift"] = CoverageSource.Gift,
    };

    public static bool TryId(string id, string path, List<ImportIssue> issues)
    {
        if (NidDataJson.IsFileId(id))
            return true;
        issues.Add(ImportIssue.Error(ImportCodes.InvalidId, "An id is lowercase letters, digits and dashes.", path + ".id"));
        return false;
    }

    public static bool TrySubscription(
        SubscriptionRecord r, string path, Func<string, ResolvedPerson?> person, List<ImportIssue> issues, out ParsedSubscription parsed)
    {
        parsed = null!;
        var ok = true;
        if (string.IsNullOrWhiteSpace(r.Name))
            ok = Invalid(issues, path + ".name", "A subscription has a name.");

        Visibility visibility = Visibility.Shared;
        if (r.Visibility == "private")
            visibility = Visibility.Private;
        else if (r.Visibility != "shared")
            ok = Invalid(issues, path + ".visibility", "Visibility is shared or private.");

        var owners = new List<Owner>();
        for (var j = 0; j < r.Ownership.Count; j++)
            if (person(r.Ownership[j].Person) is { } owner)
                owners.Add(new Owner(owner.Id, owner.Kind, r.Ownership[j].ShareBp));
            else
                ok = Unresolved(issues, $"{path}.ownership[{j}].person", r.Ownership[j].Person, "persons");

        var users = new List<PersonId>();
        for (var j = 0; j < r.ServiceUsers.Count; j++)
            if (person(r.ServiceUsers[j]) is { } user)
                users.Add(user.Id);
            else
                ok = Unresolved(issues, $"{path}.service_users[{j}]", r.ServiceUsers[j], "persons");

        ok &= TryOptionalDate(r.CancelledAt, path + ".cancelled_at", issues, out var cancelledAt);
        if (ok)
            parsed = new ParsedSubscription(visibility, owners, users, cancelledAt);
        return ok;
    }

    public static bool TryPhase(BillingPhaseRecord r, string path, Func<string, PersonId?> person, List<ImportIssue> issues, out BillingPhase phase)
    {
        phase = null!;
        var ok = TryDate(r.StartsOn, path + ".starts_on", issues, out var startsOn)
                 & TryOptionalDate(r.EndsOn, path + ".ends_on", issues, out var endsOn)
                 & TryMoney(r.Price, path + ".price", issues, out var price)
                 & TryDuration(r.Every, path + ".every", issues, out var every)
                 & TryOptionalDuration(r.Covers, path + ".covers", issues, out var covers)
                 & TryPayer(r.Payer, allowExternal: false, person, path + ".payer", issues, out var payer);

        // The engine checks a purchase's sign; a phase's is only guarded by the database, which is too late.
        if (ok && price.Amount < 0)
        {
            issues.Add(ImportIssue.Error("subscriptions.price_negative", "A phase has a price of zero or more.", path + ".price.amount"));
            ok = false;
        }

        if (ok)
            phase = new BillingPhase(BillingPhaseId.New(), startsOn, endsOn, price, every, covers, r.MaxCycles, payer);
        return ok;
    }

    public static bool TryPurchase(
        PurchaseRecord r, string path, Func<string, PersonId?> person, Func<string, ItemId?> item, List<ImportIssue> issues, out Purchase purchase)
    {
        purchase = null!;
        var ok = TryDate(r.Date, path + ".date", issues, out var date)
                 & TryMoney(r.Price, path + ".price", issues, out var price)
                 & TryPayer(r.Payer, allowExternal: true, person, path + ".payer", issues, out var payer);

        if (!Kinds.TryGetValue(r.Kind, out var kind))
            ok = Invalid(issues, path + ".kind", "A purchase is a prepaid_term, bundle, gift, adjustment or refund.");

        if (r.Grants.Count == 0)
            ok = Invalid(issues, path + ".grants", "A purchase grants at least one period.");

        var grants = new List<Grant>();
        for (var j = 0; j < r.Grants.Count; j++)
        {
            var g = r.Grants[j];
            var grantPath = $"{path}.grants[{j}]";
            var target = item(g.SubscriptionId);
            if (target is null)
                ok = Unresolved(issues, grantPath + ".subscription_id", g.SubscriptionId, "subscriptions");
            if (!Sources.TryGetValue(g.Source, out var source))
                ok = Invalid(issues, grantPath + ".source", "A grant is paid, bonus, included, trial or gift.");
            var grantOk = TryDuration(g.Duration, grantPath + ".duration", issues, out var duration)
                          & TryOptionalDate(g.StartsOn, grantPath + ".starts_on", issues, out var startsOn);
            ok &= grantOk;
            if (target is { } subscription && grantOk)
                grants.Add(new Grant(subscription, duration, source, startsOn));
        }

        if (ok)
            purchase = new Purchase(PurchaseId.New(), date, r.Label, price, payer, kind, grants, r.AllocatedAmount);
        return ok;
    }

    private static bool TryDate(string? text, string path, List<ImportIssue> issues, out LocalDate date)
    {
        if (text is not null && LocalDatePattern.Iso.Parse(text) is { Success: true } parsed)
        {
            date = parsed.Value;
            return true;
        }

        date = default;
        return Invalid(issues, path, $"'{text}' is not a date (yyyy-mm-dd).");
    }

    private static bool TryOptionalDate(string? text, string path, List<ImportIssue> issues, out LocalDate? date)
    {
        date = null;
        if (text is null)
            return true;
        if (!TryDate(text, path, issues, out var value))
            return false;
        date = value;
        return true;
    }

    private static bool TryMoney(MoneyRecord r, string path, List<ImportIssue> issues, out Money money)
    {
        money = default;
        if (!Currency.TryCreate(r.Currency, out var currency))
            return Invalid(issues, path + ".currency", $"'{r.Currency}' is not an ISO 4217 currency.");

        var created = Money.Create(r.Amount, currency);
        if (created.TryGetValue(out money))
            return true;
        created.TryGetFailure(out var failure);
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".amount"));
        return false;
    }

    private static bool TryDuration(DurationRecord r, string path, List<ImportIssue> issues, out Duration duration)
    {
        duration = default;
        if (!Units.TryGetValue(r.Unit, out var unit))
            return Invalid(issues, path + ".unit", "A unit is day, week, month or year.");

        var created = Duration.Create(r.Count, unit);
        if (created.TryGetValue(out duration))
            return true;
        created.TryGetFailure(out var failure);
        issues.Add(ImportIssue.Error(failure!.Code, failure.Message, path + ".count"));
        return false;
    }

    private static bool TryOptionalDuration(DurationRecord? r, string path, List<ImportIssue> issues, out Duration? duration)
    {
        duration = null;
        if (r is null)
            return true;
        if (!TryDuration(r, path, issues, out var value))
            return false;
        duration = value;
        return true;
    }

    private static bool TryPayer(PayerRecord r, bool allowExternal, Func<string, PersonId?> person, string path, List<ImportIssue> issues, out Payer payer)
    {
        payer = null!;
        PayerType type;
        switch (r.Type)
        {
            case "person": type = PayerType.Person; break;
            case "household": type = PayerType.Household; break;
            case "external" when allowExternal: type = PayerType.External; break;
            default:
                return Invalid(issues, path + ".type", allowExternal ? "A payer is person, household or external." : "A payer is person or household.");
        }

        if (type != PayerType.Person)
        {
            if (r.Person is not null)
                return Invalid(issues, path + ".person", "Only a payer of type person names a person.");
            payer = new Payer(type, null);
            return true;
        }

        if (r.Person is null)
            return Invalid(issues, path + ".person", "A payer of type person names the person.");
        if (person(r.Person) is not { } id)
            return Unresolved(issues, path + ".person", r.Person, "persons");

        payer = new Payer(type, id);
        return true;
    }

    private static bool Invalid(List<ImportIssue> issues, string path, string message)
    {
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, message, path));
        return false;
    }

    private static bool Unresolved(List<ImportIssue> issues, string path, string reference, string section)
    {
        issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{reference}' is not in {section}.", path));
        return false;
    }
}
