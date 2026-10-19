using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>A cost report as a body sends it (issue #34 §2.5), in percent; checked by <see cref="CostReportRules"/>.</summary>
public sealed record CostReportDraft(
    decimal? GrossReturnPct, decimal? TotalFeesPct, decimal? NetReturnPct, decimal? ContractFeesPct, decimal? SupportFeesPct,
    decimal? RetrocessionPct, decimal? NetAnnualized5yPct, string? SourceLabel);

/// <summary>Creates the holding's report of that year, or replaces it.</summary>
public sealed record SaveCostReportCommand(ItemId ItemId, Guid Holding, int Year, CostReportDraft Report) : ICommand<CostReportView>, IItemScoped
{
    public AccessKind Access => AccessKind.Write;
}

public sealed record GetCostReportQuery(ItemId ItemId, Guid Holding, int Year) : IQuery<CostReportView>, IItemScoped
{
    public AccessKind Access => AccessKind.Read;
}

/// <summary>Issue #34 §2.5, checked before anything is written: entered as stated, so only bounds and decimals.</summary>
internal static class CostReportRules
{
    public static Result<CostReport> Check(Guid holding, int year, CostReportDraft d, int thisYear)
    {
        if (year < 1900 || year > thisYear)
            return Invalid("year", "A cost report is for this year or an earlier one.");
        if (d.GrossReturnPct is null)
            return Invalid("grossReturnPct", "The gross return is required.");
        if (d.TotalFeesPct is null)
            return Invalid("totalFeesPct", "The total fees are required.");
        if (d.NetReturnPct is null)
            return Invalid("netReturnPct", "The net return is required.");
        foreach (var (value, field) in new[] { (d.GrossReturnPct, "grossReturnPct"), (d.NetReturnPct, "netReturnPct"), (d.NetAnnualized5yPct, "netAnnualized5yPct") })
            if (value is { } x && (x is < -100 or > 1000 || Math.Round(x, 4) != x))
                return Invalid(field, "A return is −100 to 1000 percent, with 4 decimals at most.");
        foreach (var (value, field) in new[] { (d.TotalFeesPct, "totalFeesPct"), (d.ContractFeesPct, "contractFeesPct"), (d.SupportFeesPct, "supportFeesPct"),
                     (d.RetrocessionPct, "retrocessionPct") })
            if (value is { } x && (x is < 0 or > 100 || Math.Round(x, 4) != x))
                return Invalid(field, "A fee is 0 to 100 percent, with 4 decimals at most.");
        if (d.SourceLabel is { Length: > HoldingRules.SourceLabelMax })
            return Invalid("sourceLabel", $"A source label has at most {HoldingRules.SourceLabelMax} characters.");
        return Result.Success(new CostReport(holding, year, d.GrossReturnPct.Value, d.TotalFeesPct.Value, d.NetReturnPct.Value, d.ContractFeesPct,
            d.SupportFeesPct, d.RetrocessionPct, d.NetAnnualized5yPct, d.SourceLabel));
    }

    private static Result<CostReport> Invalid(string field, string message)
        => Result.Failure<CostReport>(new Failure(InvestmentsCodes.InvalidCostReport, message, new Dictionary<string, object?> { ["field"] = field }));
}

[RequestHandler<SaveCostReportCommand, CostReportView>]
public sealed class SaveCostReportHandler(
    IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<SaveCostReportCommand, CostReportView>
{
    public async ValueTask<Result<CostReportView>> HandleAsync(SaveCostReportCommand command, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var loaded = await MovementFacts.LoadAsync(envelopes, items, holdings, flows, command.ItemId, write: true, command.Holding, ct);
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<CostReportView>(loaded.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        var checkedReport = CostReportRules.Check(command.Holding, command.Year, command.Report, (await calendar.TodayAsync(ct)).Year);
        if (!checkedReport.TryGetValue(out var report))
            return Result.Failure<CostReportView>(checkedReport.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        await holdings.SaveCostReportAsync(command.ItemId, report, ct);
        var implicitCost = HoldingMath.ImplicitCost(report, await holdings.SnapshotsAsync(command.ItemId, ct), facts!.Currency);
        await transaction.CommitAsync(ct);
        return Result.Success(CostReportView.Of(command.ItemId, report, implicitCost));
    }
}

[RequestHandler<GetCostReportQuery, CostReportView>]
public sealed class GetCostReportHandler(IEnvelopeStore envelopes, IItemRegistry items, IHoldingStore holdings, IFlows flows)
    : IRequestHandler<GetCostReportQuery, CostReportView>
{
    public async ValueTask<Result<CostReportView>> HandleAsync(GetCostReportQuery query, CancellationToken ct = default)
    {
        var loaded = await MovementFacts.LoadAsync(envelopes, items, holdings, flows, query.ItemId, write: false, query.Holding, ct);
        if (!loaded.TryGetValue(out var facts))
            return Result.Failure<CostReportView>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var report = (await holdings.CostReportsAsync(query.ItemId, ct)).FirstOrDefault(r => r.HoldingId == query.Holding && r.Year == query.Year);
        if (report is null)
            return Result.Failure<CostReportView>(new NotFoundFailure("cost report", $"{query.Holding}/{query.Year}"));
        return Result.Success(CostReportView.Of(query.ItemId, report, HoldingMath.ImplicitCost(report, await holdings.SnapshotsAsync(query.ItemId, ct), facts!.Currency)));
    }
}
