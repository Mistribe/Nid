using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using Nid.Documents.Application;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

public static class TaxEndpoints
{
    /// <summary>Tax estimate spec §8.</summary>
    public static IEndpointRouteBuilder MapTaxEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/tax").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (int? year, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetTaxEstimateQuery(year, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<TaxEstimateView>();

        // Issue #410.
        api.MapGet("/changes", async (int? year, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetTaxChangesQuery(year, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<TaxChangesView>();

        // Rental 2044 spec §5.
        api.MapGet("/2044", async (int? year, string? scope, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetRental2044Query(year, ParseScope(scope)), ct)).ToHttp(view => view))
            .Returns<Rental2044View>();

        // Sale tax spec §6.
        api.MapGet("/sale", async (string? scope, Guid? item, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetSaleTaxQuery(ParseScope(scope), item is { } g ? new ItemId(g) : null), ct)).ToHttp(view => view))
            .Returns<SaleTaxView>();

        api.MapPut("/profile/{year:int}", async (int year, string? scope, TaxProfileBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var profile = body.Read(reader);
                return profile is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SaveTaxProfileCommand(year, profile, ParseScope(scope)), ct)).ToHttp(view => view);
            })
            .Returns<TaxEstimateView>();

        api.MapPost("/simulate", async (TaxInputsView body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var inputs = Read(body, reader);
                return inputs is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SimulateTaxQuery(inputs), ct)).ToHttp(view => view);
            })
            .Returns<TaxResultView>();

        // Tax assessments spec §6.
        api.MapGet("/assessments", async (string? scope, int? year, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListTaxAssessmentsQuery(ParseScope(scope), year), ct)).ToHttp(list => list))
            .Returns<TaxAssessmentList>();

        // The « 10 % » deduction of a notice's declared salaries, for the form to prefill: no figure is computed in the browser.
        api.MapGet("/assessments/salary-allowance", (int? year, string? grossSalaries) =>
            {
                var reader = new FieldReader();
                if (year is null) reader.Add("year", "request.required", "The revenue year is required.");
                else if (year < TaxRules.FirstYear) reader.Add("year", "request.year", $"The rules start with the revenues of {TaxRules.FirstYear}.");
                var gross = reader.Decimal(grossSalaries, "grossSalaries");
                if (grossSalaries is null) reader.Add("grossSalaries", "request.required", "The declared salaries are required.");
                else if (gross is < 0 or >= 10_000_000m) reader.Add("grossSalaries", "request.amount", "An amount is not negative and below 10 000 000.");
                return reader.Ok ? Results.Ok(SalaryAllowanceView.Of(TaxRules.For(year!.Value), gross!.Value)) : reader.Problem();
            })
            .Returns<SalaryAllowanceView>();

        api.MapGet("/assessments/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetTaxAssessmentQuery(id), ct)).ToHttp(view => view))
            .Returns<TaxAssessmentView>();

        api.MapPost("/assessments", async (AddTaxAssessmentBody body, IInvoker invoker, ILoggerFactory logs, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var assessment = body.Read(reader);
                if (assessment is null)
                    return reader.Problem();
                if (await UnreadableSourceAsync(invoker, assessment.DocumentId, ct) is { } refused)
                    return refused;
                var added = await invoker.InvokeAsync(new AddTaxAssessmentCommand(assessment), ct);
                if (added.TryGetValue(out var view))
                    await LinkSourceAsync(invoker, logs, view!.DocumentId, view.Id, ct);
                return added.ToHttp(v => v, StatusCodes.Status201Created);
            })
            .Returns<TaxAssessmentView>(StatusCodes.Status201Created);

        api.MapPut("/assessments/{id:guid}", async (Guid id, CorrectTaxAssessmentBody body, IInvoker invoker, ILoggerFactory logs, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var assessment = body.Read(reader);
                if (assessment is null)
                    return reader.Problem();
                if (await UnreadableSourceAsync(invoker, assessment.DocumentId, ct) is { } refused)
                    return refused;
                var corrected = await invoker.InvokeAsync(new CorrectTaxAssessmentCommand(id, assessment, body.Version!.Value), ct);
                // The effective source (stored or given) follows the declarants a correction may have added; existing links are skipped.
                if (corrected.TryGetValue(out var view))
                    await LinkSourceAsync(invoker, logs, view!.DocumentId, id, ct);
                return corrected.ToHttp(v => v);
            })
            .Returns<TaxAssessmentView>();

        api.MapDelete("/assessments/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new DeleteTaxAssessmentCommand(id), ct)).ToHttp(x => new IdResponse(x)))
            .Returns<IdResponse>();

        return routes;
    }

    /// <summary>Tax assessments spec §5: a source the author cannot read answers 404 before anything is written.</summary>
    private static async Task<Microsoft.AspNetCore.Http.IResult?> UnreadableSourceAsync(IInvoker invoker, Guid? documentId, CancellationToken ct)
        => documentId is { } d && !(await invoker.InvokeAsync(new GetDocumentQuery(d), ct)).TryGetValue(out _)
            ? Result.Failure<TaxAssessmentView>(new NotFoundFailure("document", d.ToString())).ToHttp(v => v)
            : null;

    /// <summary>Links the source to each declarant after the write; a refusal (read-only document) leaves the notice saved with its source.</summary>
    private static async Task LinkSourceAsync(IInvoker invoker, ILoggerFactory logs, Guid? documentId, Guid assessmentId, CancellationToken ct)
    {
        if (documentId is not { } d)
            return;
        try
        {
            await invoker.InvokeAsync(new LinkTaxNoticeCommand(d, assessmentId), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A link failure never fails the write: the notice is saved with its source.
            logs.CreateLogger("Nid.Host.Api.TaxEndpoints").LogWarning(e, "Linking the source of tax assessment {Assessment} failed", assessmentId);
        }
    }

    /// <summary>The year's whole profile: a field left out is emptied, not kept.</summary>
    public sealed record TaxProfileBody(
        IReadOnlyDictionary<Guid, string>? Statuses, int? Dependents, int ChildrenUnder6, string? RentalRegime, IReadOnlyList<PinelCommitment>? Pinel,
        string? ChildcareOutsideHome, string? HomeEmployment, string? Donations, string? DonationsAid,
        IReadOnlyDictionary<Guid, string>? ContributionRates = null, IReadOnlyDictionary<Guid, string>? ActualExpenses = null,
        IReadOnlyDictionary<Guid, string>? PerUnusedCeilings = null, string? RentalDeficitCarried = null,
        IReadOnlyDictionary<Guid, string>? PerCeilings = null, IReadOnlyDictionary<Guid, string>? PerCeilingsExpiring = null, bool PerPooling = false,
        IReadOnlyDictionary<Guid, string>? ExemptIncomes = null, string? DonationsAidLate = null, string? RentalEnergyWorks = null,
        string? Department = null)
    {
        internal TaxProfile? Read(FieldReader reader)
        {
            var rates = (ContributionRates ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Rate(reader, x.Value, $"contributionRates.{x.Key}"));
            var expenses = (ActualExpenses ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Amount(reader, x.Value, $"actualExpenses.{x.Key}"));
            var unused = (PerUnusedCeilings ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Amount(reader, x.Value, $"perUnusedCeilings.{x.Key}"));
            var ceilings = (PerCeilings ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Amount(reader, x.Value, $"perCeilings.{x.Key}"));
            var expiring = (PerCeilingsExpiring ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Amount(reader, x.Value, $"perCeilingsExpiring.{x.Key}"));
            var exempt = (ExemptIncomes ?? new Dictionary<Guid, string>()).ToDictionary(x => x.Key, x => Amount(reader, x.Value, $"exemptIncomes.{x.Key}"));
            var carried = Amount(reader, RentalDeficitCarried, "rentalDeficitCarried");
            var childcare = Amount(reader, ChildcareOutsideHome, "childcareOutsideHome");
            var home = Amount(reader, HomeEmployment, "homeEmployment");
            var donations = Amount(reader, Donations, "donations");
            var aid = Amount(reader, DonationsAid, "donationsAid");
            var aidLate = Amount(reader, DonationsAidLate, "donationsAidLate");
            var energy = Amount(reader, RentalEnergyWorks, "rentalEnergyWorks");
            return reader.Ok
                ? new TaxProfile(Statuses ?? new Dictionary<Guid, string>(), Dependents, ChildrenUnder6, RentalRegime, Pinel ?? [], childcare, home, donations, aid,
                    rates, expenses, unused, carried, ceilings, expiring, PerPooling, exempt, aidLate, energy,
                    string.IsNullOrWhiteSpace(Department) ? null : Department.Trim().ToUpperInvariant())
                : null;
        }
    }

    /// <summary>
    /// Tax assessments spec §6: one declarant of a notice. <c>Deduction</c> is <c>allowance</c> or <c>actual</c>; amounts are euros.
    /// Every figure left out stays absent.
    /// </summary>
    public sealed record TaxAssessmentDeclarantBody(
        Guid? PersonId, int? Rank, MoneyBody? GrossSalaries, string? Deduction, MoneyBody? DeductionAmount, MoneyBody? NetSalaries,
        MoneyBody? PerCeilingYear, MoneyBody? PerUnusedOldest, MoneyBody? PerUnusedMiddle, MoneyBody? PerUnusedLatest,
        MoneyBody? PerCeilingComputed, MoneyBody? PerCeilingNext, MoneyBody? OvertimeExempt = null, MoneyBody? Withholding = null,
        MoneyBody? CreditsAdvance = null);

    /// <summary>
    /// Tax assessments spec §6: a notice, field names as the view's. <c>IssuedYear</c> left out is <c>Year + 1</c>; rates are percent strings.
    /// </summary>
    public sealed record AddTaxAssessmentBody(
        int? Year, int? IssuedYear, IReadOnlyList<TaxAssessmentDeclarantBody>? Declarants, string? Shares, MoneyBody? ReferenceIncome,
        string? AverageRate, string? MarginalRate, MoneyBody? GrossIncome, MoneyBody? TaxableIncome, MoneyBody? CapitalIncomeDeclared,
        MoneyBody? CapitalIncomeTaxable, MoneyBody? RentalIncomeNet, MoneyBody? FlatRateIncome, MoneyBody? ExemptIncomeDeclared,
        MoneyBody? ExemptIncomeNet, MoneyBody? ScaleTax, MoneyBody? ProportionalTax, MoneyBody? TaxBeforeCredits, MoneyBody? CreditsDeclared,
        MoneyBody? CreditsRetained, MoneyBody? NetTax, MoneyBody? Withholding, MoneyBody? Balance, MoneyBody? RentalDeficitCarried, Guid? DocumentId = null,
        MoneyBody? DividendsEligible = null, MoneyBody? CapitalIncomeAlreadyLevied = null, MoneyBody? FlatRatePrepaidCredit = null,
        MoneyBody? RentalDeficitOnRentalIncome = null, MoneyBody? RentalDeficitOnGlobalIncome = null, MoneyBody? PinelReduction = null,
        MoneyBody? ChildcareExpenses = null)
    {
        internal TaxAssessment? Read(FieldReader reader)
        {
            if (Year is null) reader.Add("year", "request.required", "The revenue year is required.");
            var declarants = new List<TaxAssessmentDeclarant>();
            for (var i = 0; i < (Declarants?.Count ?? 0); i++)
            {
                var d = Declarants![i];
                var p = $"declarants[{i}]";
                if (d is null) { reader.Add(p, "request.required", "A declarant is required."); continue; }
                if (d.PersonId is null) reader.Add($"{p}.personId", "request.required", "A declarant is a person.");
                if (d.Rank is null) reader.Add($"{p}.rank", "request.required", "A rank is 1 or 2.");
                SalaryDeduction? deduction = null;
                if (d.Deduction is not null)
                {
                    if (Enum.TryParse<SalaryDeduction>(d.Deduction, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) deduction = parsed;
                    else reader.Add($"{p}.deduction", "request.deduction", "A deduction is allowance or actual.");
                }
                declarants.Add(new TaxAssessmentDeclarant(
                    new PersonId(d.PersonId ?? Guid.Empty), d.Rank ?? 0, Euros(reader, d.GrossSalaries, $"{p}.grossSalaries"), deduction,
                    Euros(reader, d.DeductionAmount, $"{p}.deductionAmount"), Euros(reader, d.NetSalaries, $"{p}.netSalaries"),
                    Euros(reader, d.PerCeilingYear, $"{p}.perCeilingYear"), Euros(reader, d.PerUnusedOldest, $"{p}.perUnusedOldest"),
                    Euros(reader, d.PerUnusedMiddle, $"{p}.perUnusedMiddle"), Euros(reader, d.PerUnusedLatest, $"{p}.perUnusedLatest"),
                    Euros(reader, d.PerCeilingComputed, $"{p}.perCeilingComputed"), Euros(reader, d.PerCeilingNext, $"{p}.perCeilingNext"),
                    Euros(reader, d.OvertimeExempt, $"{p}.overtimeExempt"), Euros(reader, d.Withholding, $"{p}.withholding"),
                    Euros(reader, d.CreditsAdvance, $"{p}.creditsAdvance")));
            }
            var assessment = new TaxAssessment(
                Year ?? 0, IssuedYear ?? (Year ?? 0) + 1, declarants, reader.Decimal(Shares, "shares"), Euros(reader, ReferenceIncome, "referenceIncome"),
                reader.Decimal(AverageRate, "averageRate"), reader.Decimal(MarginalRate, "marginalRate"), Euros(reader, GrossIncome, "grossIncome"),
                Euros(reader, TaxableIncome, "taxableIncome"), Euros(reader, CapitalIncomeDeclared, "capitalIncomeDeclared"),
                Euros(reader, CapitalIncomeTaxable, "capitalIncomeTaxable"), Euros(reader, RentalIncomeNet, "rentalIncomeNet"),
                Euros(reader, FlatRateIncome, "flatRateIncome"), Euros(reader, ExemptIncomeDeclared, "exemptIncomeDeclared"),
                Euros(reader, ExemptIncomeNet, "exemptIncomeNet"), Euros(reader, ScaleTax, "scaleTax"), Euros(reader, ProportionalTax, "proportionalTax"),
                Euros(reader, TaxBeforeCredits, "taxBeforeCredits"), Euros(reader, CreditsDeclared, "creditsDeclared"),
                Euros(reader, CreditsRetained, "creditsRetained"), Euros(reader, NetTax, "netTax"), Euros(reader, Withholding, "withholding"),
                Euros(reader, Balance, "balance"), Euros(reader, RentalDeficitCarried, "rentalDeficitCarried"), DocumentId,
                Euros(reader, DividendsEligible, "dividendsEligible"), Euros(reader, CapitalIncomeAlreadyLevied, "capitalIncomeAlreadyLevied"),
                Euros(reader, FlatRatePrepaidCredit, "flatRatePrepaidCredit"), Euros(reader, RentalDeficitOnRentalIncome, "rentalDeficitOnRentalIncome"),
                Euros(reader, RentalDeficitOnGlobalIncome, "rentalDeficitOnGlobalIncome"), Euros(reader, PinelReduction, "pinelReduction"),
                Euros(reader, ChildcareExpenses, "childcareExpenses"));
            return reader.Ok ? assessment : null;
        }

        /// <summary>An amount in euros, null when absent; another currency is refused at its <c>currency</c>.</summary>
        private static decimal? Euros(FieldReader reader, MoneyBody? body, string path)
        {
            if (body is null) return null;
            if (reader.Money(body, path) is not { } money) return null;
            if (money.Currency.Code == "EUR") return money.Amount;
            reader.Add(path + ".currency", "request.currency", "A tax notice is in EUR.");
            return null;
        }
    }

    /// <summary>
    /// Tax assessments spec §6: the notice's fields again and the <c>version</c> read with it. A missing version answers 422 at
    /// <c>version</c>: it would otherwise bind to 0 and pass for a conflict.
    /// </summary>
    public sealed record CorrectTaxAssessmentBody(
        int? Year, int? IssuedYear, IReadOnlyList<TaxAssessmentDeclarantBody>? Declarants, string? Shares, MoneyBody? ReferenceIncome,
        string? AverageRate, string? MarginalRate, MoneyBody? GrossIncome, MoneyBody? TaxableIncome, MoneyBody? CapitalIncomeDeclared,
        MoneyBody? CapitalIncomeTaxable, MoneyBody? RentalIncomeNet, MoneyBody? FlatRateIncome, MoneyBody? ExemptIncomeDeclared,
        MoneyBody? ExemptIncomeNet, MoneyBody? ScaleTax, MoneyBody? ProportionalTax, MoneyBody? TaxBeforeCredits, MoneyBody? CreditsDeclared,
        MoneyBody? CreditsRetained, MoneyBody? NetTax, MoneyBody? Withholding, MoneyBody? Balance, MoneyBody? RentalDeficitCarried, uint? Version, Guid? DocumentId = null,
        MoneyBody? DividendsEligible = null, MoneyBody? CapitalIncomeAlreadyLevied = null, MoneyBody? FlatRatePrepaidCredit = null,
        MoneyBody? RentalDeficitOnRentalIncome = null, MoneyBody? RentalDeficitOnGlobalIncome = null, MoneyBody? PinelReduction = null,
        MoneyBody? ChildcareExpenses = null)
    {
        internal TaxAssessment? Read(FieldReader reader)
        {
            var assessment = new AddTaxAssessmentBody(
                Year, IssuedYear, Declarants, Shares, ReferenceIncome, AverageRate, MarginalRate, GrossIncome, TaxableIncome, CapitalIncomeDeclared,
                CapitalIncomeTaxable, RentalIncomeNet, FlatRateIncome, ExemptIncomeDeclared, ExemptIncomeNet, ScaleTax, ProportionalTax, TaxBeforeCredits,
                CreditsDeclared, CreditsRetained, NetTax, Withholding, Balance, RentalDeficitCarried, DocumentId, DividendsEligible,
                CapitalIncomeAlreadyLevied, FlatRatePrepaidCredit, RentalDeficitOnRentalIncome, RentalDeficitOnGlobalIncome, PinelReduction,
                ChildcareExpenses).Read(reader);
            if (Version is null) reader.Add("version", "request.required", "The version read with the tax assessment is required.");
            return reader.Ok ? assessment : null;
        }
    }

    private static TaxInputs? Read(TaxInputsView body, FieldReader reader)
    {
        var declarants = new List<TaxDeclarant>();
        for (var i = 0; i < (body.Declarants?.Count ?? 0); i++)
        {
            var d = body.Declarants![i];
            if (!TaxEstimates.TryStatus(d.Status, out var status))
                reader.Add($"declarants[{i}].status", "request.status", "A status is non_executive, executive or public.");
            declarants.Add(new TaxDeclarant(d.Name ?? "", Amount(reader, d.SalaryGross, $"declarants[{i}].salaryGross"), status,
                Amount(reader, d.OtherTaxable, $"declarants[{i}].otherTaxable"), Amount(reader, d.PerContributions, $"declarants[{i}].perContributions"),
                d.ContributionRate is null ? null : Rate(reader, d.ContributionRate, $"declarants[{i}].contributionRate"),
                d.ActualExpenses is null ? null : Amount(reader, d.ActualExpenses, $"declarants[{i}].actualExpenses"),
                Amount(reader, d.PerUnusedCeiling, $"declarants[{i}].perUnusedCeiling"),
                Amount(reader, d.SalaryTaxable, $"declarants[{i}].salaryTaxable"),
                d.PerCeiling is null ? null : Amount(reader, d.PerCeiling, $"declarants[{i}].perCeiling"),
                Amount(reader, d.PreviousYearIncome, $"declarants[{i}].previousYearIncome"),
                Amount(reader, d.PerExpiring, $"declarants[{i}].perExpiring"),
                Amount(reader, d.ExemptIncome, $"declarants[{i}].exemptIncome")));
        }
        RentalRegime? regime = null;
        if (body.RentalRegime is not null)
        {
            if (TaxEstimates.TryRegime(body.RentalRegime, out var r)) regime = r;
            else reader.Add("rentalRegime", "request.regime", "A rental regime is micro or real.");
        }
        var inputs = new TaxInputs(
            body.Year, declarants, body.Dependents, body.ChildrenUnder6,
            Amount(reader, body.RentalGross, "rentalGross"), Amount(reader, body.RentalInterest, "rentalInterest"),
            Amount(reader, body.RentalCharges, "rentalCharges"), regime,
            Amount(reader, body.CapitalIncome, "capitalIncome"), Amount(reader, body.NonTaxableIncome, "nonTaxableIncome"),
            Amount(reader, body.PinelReduction, "pinelReduction"), Amount(reader, body.ChildcareOutsideHome, "childcareOutsideHome"),
            Amount(reader, body.HomeEmployment, "homeEmployment"), Amount(reader, body.Donations, "donations"), Amount(reader, body.DonationsAid, "donationsAid"),
            Amount(reader, body.RentalDeficitCarried, "rentalDeficitCarried"), body.PerPooling,
            Amount(reader, body.DonationsAidLate, "donationsAidLate"), Amount(reader, body.RentalEnergyWorks, "rentalEnergyWorks"));
        return reader.Ok ? inputs : null;
    }

    /// <summary>A percent string (<c>"23.5"</c>) between 0 and 60, as a fraction.</summary>
    private static decimal Rate(FieldReader reader, string value, string path)
    {
        var percent = reader.Decimal(value, path);
        if (percent is { } p && TaxEstimates.RateOk(p / 100)) return p / 100;
        if (percent is not null) reader.Add(path, "request.rate", "A contribution rate is a percent between 0 and 60.");
        return 0m;
    }

    /// <summary>Absent is 0; an amount is a non-negative decimal string below ten million.</summary>
    private static decimal Amount(FieldReader reader, string? value, string path)
    {
        if (value is null) return 0m;
        var amount = reader.Decimal(value, path);
        if (amount is >= 0 and < 10_000_000m) return amount.Value;
        if (amount is not null) reader.Add(path, "request.amount", "An amount is not negative and below 10 000 000.");
        return 0m;
    }
}
