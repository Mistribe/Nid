using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application.Import;

/// <summary>A property record, parsed; owners carry their kind so the Core's rules can run on it.</summary>
internal sealed record ParsedProperty(PropertyDetails Details, Currency Currency, Visibility Visibility, IReadOnlyList<Owner> Owners);

/// <summary>
/// Spec 3F1 §2: file records to real estate inputs. The rules are the screens' (<see cref="PropertyRules"/>, <see cref="LoanRules"/>,
/// <see cref="FeeCheck"/>, <see cref="RepaymentCheck"/>, the schedule engine), never restated: their API field names become the
/// file's members (<see cref="Member"/>). An entity's issues are all collected (the non-short-circuit <c>&amp;</c>). A
/// <c>currency</c> is null on receipt: a batch alone does not know its property or loan, nor the household's today.
/// </summary>
internal static partial class EstateParsing
{
    /// <summary>The rules' fields that hold an amount: a refusal names its <c>.amount</c>.</summary>
    private static readonly HashSet<string> Amounts = new(StringComparer.Ordinal)
    {
        "purchasePrice", "notaryFees", "agencyFees", "otherAcquisitionCosts", "amount", "rentMonthly", "chargesMonthly", "deposit",
        "principal", "paymentOverride", "openingBalanceOverride", "monthlyAmount",
    };

    private static readonly Dictionary<string, DurationUnit> Units = new(StringComparer.Ordinal)
    {
        ["day"] = DurationUnit.Day, ["week"] = DurationUnit.Week, ["month"] = DurationUnit.Month, ["year"] = DurationUnit.Year,
    };

    /// <summary>
    /// A property's own rules: <see cref="PropertyRules.Name"/> and <see cref="PropertyRules.Details"/>. <paramref name="today"/> is
    /// null on receipt: the purchase's « not after today » then waits for the validation.
    /// </summary>
    public static bool TryProperty(PropertyRecord r, string path, Func<string, ResolvedPerson?> person, LocalDate? today,
        List<ImportIssue> issues, out ParsedProperty parsed)
    {
        parsed = null!;
        var currency = TryCurrency(r.Currency, path, issues);
        var ok = currency is not null
                 & TryVisibility(r.Visibility, path, issues, out var visibility)
                 & TryOwners(r.Ownership, path, person, issues, out var owners)
                 & TryWire<PropertyType>(r.PropertyType, path + ".property_type", issues, out var type)
                 & TryWire<PropertyCadence>(r.ValuationCadence, path + ".valuation_cadence", issues, out var cadence)
                 & TryDate(r.PurchaseDate, path + ".purchase_date", issues, out var purchaseDate)
                 & TryDate(r.RentalStartsOn, path + ".rental_starts_on", issues, out var rentalStartsOn)
                 & TryMoney(r.PurchasePrice, path + ".purchase_price", currency, issues, out var price)
                 & TryMoney(r.NotaryFees, path + ".notary_fees", currency, issues, out var notary)
                 & TryMoney(r.AgencyFees, path + ".agency_fees", currency, issues, out var agency)
                 & TryMoney(r.OtherAcquisitionCosts, path + ".other_acquisition_costs", currency, issues, out var other);
        if (!ok)
            return false;
        var details = new PropertyDetails(type, purchaseDate!.Value, price!.Value, notary ?? 0m, agency ?? 0m, other ?? 0m, r.SurfaceM2,
            r.LocationLabel, cadence, r.StaleAfterDays, rentalStartsOn, r.Note);
        if (!Check(PropertyRules.Name(r.Name) ?? PropertyRules.Details(details, today ?? LocalDate.MaxIsoValue), path, issues))
            return false;
        parsed = new ParsedProperty(details, currency!.Value, visibility, owners);
        return true;
    }

    /// <summary>A planned cost's rules: <see cref="PropertyRules.CostFields"/>; a unit the Core does not know is no period at all.</summary>
    public static bool TryCost(PropertyRecurringCostRecord r, string path, Currency? currency, List<ImportIssue> issues, out RecurringCost cost)
    {
        cost = null!;
        var ok = TryMoney(r.Amount, path + ".amount", currency, issues, out var amount)
                 & TryDate(r.StartsOn, path + ".starts_on", issues, out var startsOn)
                 & TryDate(r.EndsOn, path + ".ends_on", issues, out var endsOn);
        if (!Units.TryGetValue(r.Every.Unit, out var unit))
            ok = Invalid(issues, path + ".every.unit", "A period's unit is day, week, month or year.");
        if (!ok)
            return false;
        var draft = new RecurringCostDraft(r.Category, amount!.Value, r.Every.Count, unit, startsOn!.Value, endsOn, r.Note);
        if (!Check(PropertyRules.CostFields(draft), path, issues))
            return false;
        cost = draft.ToCost();
        return true;
    }

    /// <summary>
    /// A tenancy's own rules: <see cref="PropertyRules.TenancyFields"/> alone. Its property's purchase and its other tenancies are
    /// the whole file's (<see cref="RealEstateImportSection"/>), as the screens check them against the property as stored.
    /// </summary>
    public static bool TryTenancy(TenancyRecord r, string path, Currency? currency, List<ImportIssue> issues, out Tenancy tenancy)
    {
        tenancy = null!;
        var ok = TryDate(r.StartsOn, path + ".starts_on", issues, out var startsOn)
                 & TryDate(r.EndsOn, path + ".ends_on", issues, out var endsOn)
                 & TryMoney(r.RentMonthly, path + ".rent_monthly", currency, issues, out var rent)
                 & TryMoney(r.ChargesMonthly, path + ".charges_monthly", currency, issues, out var charges)
                 & TryMoney(r.Deposit, path + ".deposit", currency, issues, out var deposit);
        if (!ok)
            return false;
        var parsed = new Tenancy(r.Label, startsOn!.Value, endsOn, rent!.Value, charges ?? 0m, deposit, r.Note);
        if (!Check(PropertyRules.TenancyFields(parsed, LocalDate.MinIsoValue), path, issues))
            return false;
        tenancy = parsed;
        return true;
    }

    /// <summary>A refusal of the screens' rules, at the file member its field names; <paramref name="strip"/> drops a prefix the rule adds (<c>phases[0].</c>).</summary>
    public static bool Check(Failure? failure, string path, List<ImportIssue> issues, string strip = "")
    {
        if (failure is null)
            return true;
        var field = LoanPaths.FieldOf(failure) ?? "";
        if (strip.Length > 0 && field.StartsWith(strip, StringComparison.Ordinal))
            field = field[strip.Length..];
        issues.Add(ImportIssue.Error(failure.Code, failure.Message, path + Member(field)));
        return false;
    }

    /// <summary>A rule's field (the API body's camelCase, dotted for a period) as the file's member: <c>purchasePrice</c> → <c>.purchase_price.amount</c>.</summary>
    public static string Member(string field)
        => field.Length == 0
            ? ""
            : "." + string.Join('.', field.Split('.').Select(JsonNamingPolicy.SnakeCaseLower.ConvertName)) + (Amounts.Contains(field) ? ".amount" : "");

    private static Currency? TryCurrency(string code, string path, List<ImportIssue> issues)
    {
        if (Currency.TryCreate(code, out var currency))
            return currency;
        Invalid(issues, path + ".currency", "A currency is an ISO 4217 code in capitals.");
        return null;
    }

    private static bool TryVisibility(string visibility, string path, List<ImportIssue> issues, out Visibility parsed)
    {
        parsed = visibility == "private" ? Visibility.Private : Visibility.Shared;
        return visibility is "private" or "shared" || Invalid(issues, path + ".visibility", "Visibility is shared or private.");
    }

    private static bool TryOwners(IReadOnlyList<ShareRecord> ownership, string path, Func<string, ResolvedPerson?> person,
        List<ImportIssue> issues, out IReadOnlyList<Owner> owners)
    {
        var found = new List<Owner>();
        var ok = true;
        for (var j = 0; j < ownership.Count; j++)
            if (person(ownership[j].Person) is { } owner)
                found.Add(new Owner(owner.Id, owner.Kind, ownership[j].ShareBp));
            else
            {
                issues.Add(ImportIssue.Error(ImportCodes.UnresolvedReference, $"'{ownership[j].Person}' is not in persons.", $"{path}.ownership[{j}].person"));
                ok = false;
            }
        owners = found;
        return ok;
    }

    private static bool TryWire<T>(string wire, string path, List<ImportIssue> issues, out T value) where T : struct, Enum
        => LoanWire.TryParse(wire, out value)
           || Invalid(issues, path, $"One of {string.Join(", ", Enum.GetValues<T>().Select(v => LoanWire.Of(v)))}.");

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

    /// <summary>An amount in its property's or loan's currency (<paramref name="expected"/>, null on receipt); a mismatch is reported at <c>.currency</c>.</summary>
    private static bool TryMoney(MoneyRecord? r, string path, Currency? expected, List<ImportIssue> issues, out decimal? amount)
    {
        amount = null;
        if (r is null)
            return true;
        if (!Currency.TryCreate(r.Currency, out var currency))
            return Invalid(issues, path + ".currency", "A currency is an ISO 4217 code in capitals.");
        if (expected is { } e && currency != e)
        {
            issues.Add(ImportIssue.Error(CoreFactCodes.CurrencyMismatch, $"This amount is in the currency of its property or loan, {e.Code}.", path + ".currency"));
            return false;
        }
        amount = r.Amount;
        return true;
    }

    private static bool Invalid(List<ImportIssue> issues, string path, string message)
    {
        issues.Add(ImportIssue.Error(ImportCodes.InvalidValue, message, path));
        return false;
    }
}
