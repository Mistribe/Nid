using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>2B1a spec §7: the figures from the Core facts, as the ambient actor.</summary>
public sealed class EnvelopePerformance(
    IEnvelopeStore envelopes, IItemRegistry items, IValuations valuations, IFlows flows, IHouseholdCalendar calendar)
    : IEnvelopePerformance
{
    public async Task<Result<EnvelopeFigures>> GetAsync(ItemId envelope, LocalDate? on, CancellationToken ct)
    {
        var facts = await FactsAsync(envelope, ct);
        if (facts.TryGetFailure(out var failure))
            return Result.Failure<EnvelopeFigures>(failure!);
        facts.TryGetValue(out var f);
        var d = on ?? await calendar.TodayAsync(ct);
        var figures = EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, d);
        // Minor 2: a closed envelope reports no staleness — EnvelopeMath stays pure and unaware of "closed";
        // this is where "closed" is known.
        return Result.Success(f.Item.ArchivedAt is not null ? figures with { StaleDays = null } : figures);
    }

    public async Task<Result<PeriodPerformance>> PeriodAsync(ItemId envelope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        if (from > to)
            return Result.Failure<PeriodPerformance>(new UnambitiousFx.Functional.Failures.Failure(
                InvestmentsCodes.InvalidPeriod, "The period's start is after its end.", null));
        var facts = await FactsAsync(envelope, ct);
        if (facts.TryGetFailure(out var failure))
            return Result.Failure<PeriodPerformance>(failure!);
        facts.TryGetValue(out var f);
        return Result.Success(EnvelopeMath.Period(f!.Values, f.Flows, f.Currency, from, to));
    }

    internal async Task<Result<Facts>> FactsAsync(ItemId id, CancellationToken ct)
    {
        var loaded = await EnvelopeCheck.LoadAsync(envelopes, items, id, ct);
        if (loaded.TryGetFailure(out var failure))
            return Result.Failure<Facts>(failure!);
        loaded.TryGetValue(out var envelope);
        var series = await valuations.SeriesAsync(id, EnvelopeCheck.Everything, ct);
        if (series.TryGetFailure(out failure))
            return Result.Failure<Facts>(failure!);
        var list = await flows.ListAsync(id, EnvelopeCheck.Everything, null, ct);
        if (list.TryGetFailure(out failure))
            return Result.Failure<Facts>(failure!);
        series.TryGetValue(out var values);
        list.TryGetValue(out var flowList);
        var currency = values!.FirstOrDefault()?.Value.Currency ?? flowList!.FirstOrDefault()?.Amount.Currency;
        if (currency is null)
        {
            // An envelope with no fact yet: its currency is the item's (the Core refuses an item without one
            // for facts, 2A). Fix round 1, ruling 2: a failed sum propagates as FactsAsync's own failure —
            // never a silent EUR fallback that would misreport a household in another currency.
            var sum = await flows.SumAsync(id, new HashSet<FlowKind> { FlowKind.Contribution }, EnvelopeCheck.Everything, ct);
            if (sum.TryGetFailure(out failure))
                return Result.Failure<Facts>(failure!);
            sum.TryGetValue(out var zero);
            currency = zero.Currency;
        }
        return Result.Success(new Facts(envelope.Envelope, envelope.Item, values!, flowList!, currency!.Value));
    }

    internal sealed record Facts(Envelope Envelope, ItemAccess Item, IReadOnlyList<Valuation> Values, IReadOnlyList<Flow> Flows, Currency Currency);
}
