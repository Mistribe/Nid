using System.Globalization;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application.Import;

/// <summary>A loan record, parsed: its terms; the phases and insurances come with the whole loan.</summary>
internal sealed record ParsedLoan(
    Currency Currency, Visibility Visibility, IReadOnlyList<Owner> Owners, LoanContract Terms, ScheduleSource Source,
    ShareLevel ShareLevel = ShareLevel.View);

internal static partial class EstateParsing
{
    /// <summary>
    /// A loan's own rules: <see cref="LoanRules.Name"/>, <see cref="LoanRules.Details"/> and the terms' bounds
    /// (<see cref="LoanRules.Contract"/> without a phase). The whole loan is the engine's, at validation.
    /// </summary>
    public static bool TryLoan(LoanRecord r, string path, Func<string, ResolvedPerson?> person, List<ImportIssue> issues, out ParsedLoan parsed)
    {
        parsed = null!;
        var currency = TryCurrency(r.Currency, path, issues);
        var ok = currency is not null
                 & TryVisibility(r.Visibility, path, issues, out var visibility)
                 & TryShareLevel(r.ShareLevel, r.Visibility, path, issues, out var level)
                 & TryOwners(r.Ownership, path, person, issues, out var owners)
                 & TryWire<InterestRounding>(r.RoundingMode, path + ".rounding_mode", issues, out var rounding)
                 & TryWire<FirstPeriod>(r.FirstPeriodMode, path + ".first_period_mode", issues, out var firstPeriod)
                 & TryDate(r.DisbursedOn, path + ".disbursed_on", issues, out var disbursedOn)
                 & TryDate(r.FirstPaymentDate, path + ".first_payment_date", issues, out var firstPayment)
                 & TryMoney(r.Principal, path + ".principal", currency, issues, out var principal)
                 & TrySource(r.ScheduleSource, path + ".schedule_source", issues, out var source);
        if (!ok)
            return false;
        var terms = new LoanContract(principal!.Value, disbursedOn!.Value, firstPayment!.Value, r.TotalPayments, rounding, firstPeriod, [], []);
        if (!Check(LoanRules.Name(r.Name) ?? LoanRules.Details(r.Lender, r.Note) ?? LoanRules.Contract(terms), path, issues))
            return false;
        parsed = new ParsedLoan(currency!.Value, visibility, owners, terms, source, level);
        return true;
    }

    public static bool TryPhase(LoanPhaseRecord r, string path, Currency? currency, List<ImportIssue> issues, out LoanPhaseEntry entry)
    {
        entry = null!;
        var ok = TryWire<PhaseMode>(r.Mode, path + ".mode", issues, out var mode)
                 & TryWire<RateType>(r.RateType, path + ".rate_type", issues, out var rateType)
                 & TryMoney(r.PaymentOverride, path + ".payment_override", currency, issues, out var payment)
                 & TryMoney(r.OpeningBalanceOverride, path + ".opening_balance_override", currency, issues, out var opening);
        if (!ok)
            return false;
        var parsed = new LoanPhaseEntry(new LoanPhase(r.Seq, mode, r.AnnualRate, rateType, r.Payments, payment, opening), r.Note);
        if (!Check(LoanRules.Contract(Alone([parsed], [])), path, issues, strip: "phases[0]."))
            return false;
        entry = parsed;
        return true;
    }

    public static bool TryInsurance(LoanInsuranceRecord r, string path, Currency? currency, List<ImportIssue> issues, out LoanInsuranceEntry entry)
    {
        entry = null!;
        var ok = TryWire<InsuranceMode>(r.Mode, path + ".mode", issues, out var mode)
                 & TryMoney(r.MonthlyAmount, path + ".monthly_amount", currency, issues, out var monthly)
                 & TryDate(r.StartsOn, path + ".starts_on", issues, out var startsOn)
                 & TryDate(r.EndsOn, path + ".ends_on", issues, out var endsOn);
        if (!ok)
            return false;
        var parsed = new LoanInsuranceEntry(new LoanInsurance(mode, monthly, r.AnnualRate, startsOn!.Value, endsOn), r.Note);
        if (!Check(LoanRules.Contract(Alone([], [parsed])), path, issues, strip: "insurances[0]."))
            return false;
        entry = parsed;
        return true;
    }

    /// <summary>A fee's rules: <see cref="FeeCheck.Rules"/>. Its id is the section's to give.</summary>
    public static bool TryFee(LoanFeeRecord r, string path, Currency? currency, List<ImportIssue> issues, out StoredFee fee)
    {
        fee = null!;
        var ok = TryWire<FeeKind>(r.Kind, path + ".kind", issues, out var kind)
                 & TryMoney(r.Amount, path + ".amount", currency, issues, out var amount)
                 & TryDate(r.OnDate, path + ".on_date", issues, out var onDate);
        if (!ok)
            return false;
        var draft = new FeeDraft(kind, amount!.Value, onDate!.Value, r.Note);
        if (!Check(FeeCheck.Rules(draft), path, issues))
            return false;
        fee = new StoredFee(Guid.Empty, FeeCheck.Fee(draft), r.Note, 0);
        return true;
    }

    /// <summary>An early repayment's rules: <see cref="RepaymentCheck.Rules"/>, without a penalty. Its id is the section's to give.</summary>
    public static bool TryEvent(LoanEventRecord r, string path, Currency? currency, List<ImportIssue> issues, out StoredRepayment repayment)
    {
        repayment = null!;
        var ok = TryWire<RepaymentEffect>(r.Effect, path + ".effect", issues, out var effect)
                 & TryMoney(r.Amount, path + ".amount", currency, issues, out var amount)
                 & TryDate(r.OnDate, path + ".on_date", issues, out var onDate);
        if (!ok)
            return false;
        var draft = new RepaymentDraft(onDate!.Value, amount!.Value, effect, null, r.Note);
        if (!Check(RepaymentCheck.Rules(draft), path, issues))
            return false;
        repayment = new StoredRepayment(Guid.Empty, new EarlyRepayment(draft.OnDate, draft.Amount, draft.Effect), null, r.Note, 0);
        return true;
    }

    /// <summary>Spec 3F2 §3: a row of a bank's table, its date read; its rules are its loan's, the whole table checked with it.</summary>
    public static bool TryBankRow(LoanBankScheduleRecord r, string path, List<ImportIssue> issues, out BankRow row)
    {
        row = null!;
        if (!TryDate(r.DueDate, path + ".due_date", issues, out var dueDate))
            return false;
        row = new BankRow(dueDate!.Value, r.Payment, r.Interest, r.Principal, r.Insurance, r.Crd);
        return true;
    }

    /// <summary>
    /// A refusal of <see cref="LoanRules.Contract"/>, of the engine or of the bank's table on a whole loan, at the file entity that
    /// brings the part at fault: <c>phases[k]</c> is the k-th of <paramref name="phases"/>, and so on for the insurances, fees,
    /// events and the table's <paramref name="rows"/> (by date) — the lists the checks were handed, in their order. A part the file
    /// does not name (null), a term, or the loan as a whole answers at <paramref name="loan"/>.
    /// </summary>
    public static string PathOf(Failure failure, string loan, IReadOnlyList<string?> phases, IReadOnlyList<string?> insurances,
        IReadOnlyList<string?> fees, IReadOnlyList<string?> events, IReadOnlyList<string?>? rows = null)
    {
        var field = LoanPaths.FieldOf(failure) ?? "";
        var open = field.IndexOf('[');
        if (open < 0)
            return loan + (field is "phases" or "insurances" or "rows" ? "" : Member(field));
        var close = field.IndexOf(']');
        var index = int.Parse(field[(open + 1)..close], CultureInfo.InvariantCulture);
        var parts = field[..open] switch { "phases" => phases, "insurances" => insurances, "fees" => fees, "rows" => rows ?? [], _ => events };
        var tail = close + 2 <= field.Length ? field[(close + 2)..] : "";
        // A bank row's amounts are plain decimals, not { amount, currency }: its principal has no ".amount".
        var member = field[..open] == "rows" && tail.Length > 0 ? "." + JsonNamingPolicy.SnakeCaseLower.ConvertName(tail) : Member(tail);
        return index < parts.Count && parts[index] is { } path ? path + member : loan;
    }

    /// <summary>Spec 3F2 §3: <c>computed</c> (or absent) or <c>bank</c>; <c>bank</c> needs the loan's table, checked with the whole loan.</summary>
    private static bool TrySource(string? source, string path, List<ImportIssue> issues, out ScheduleSource value)
    {
        value = ScheduleSource.Computed;
        return source is null || LoanWire.TryParse(source, out value) || Invalid(issues, path, "A schedule source is computed or bank.");
    }

    /// <summary>A contract holding only the parts given, on neutral terms: what <see cref="LoanRules.Contract"/> checks of one part alone.</summary>
    private static LoanContract Alone(IReadOnlyList<LoanPhaseEntry> phases, IReadOnlyList<LoanInsuranceEntry> insurances)
        => new(1m, new LocalDate(2000, 1, 1), new LocalDate(2000, 1, 1), 1, InterestRounding.HalfUp, FirstPeriod.Full, phases, insurances);
}
