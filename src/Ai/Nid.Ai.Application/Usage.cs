using Nid.Ai.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Ai.Application;

/// <summary>One model call as a page of <c>llm_call</c> lists it (§11): no content, never another adult's call.</summary>
public sealed record LlmCallItem(
    Guid Id, Instant CreatedAt, string Use, string? Model, string? Provider, Guid? WorkerId, int TokensIn, int TokensOut, decimal? CostEur,
    string Status, string? Code);

/// <summary>The household's month: how many calls, how many tokens, what the paid ones cost.</summary>
public sealed record LlmMonthTotals(int Calls, long TokensIn, long TokensOut, decimal CostEur);

public interface ILlmCallReads
{
    /// <summary>The person's own calls, newest first, and how many there are.</summary>
    Task<(IReadOnlyList<LlmCallItem> Items, int Total)> PageAsync(Guid person, int skip, int take, CancellationToken ct);

    Task<LlmMonthTotals> MonthAsync(Instant from, Instant to, CancellationToken ct);
}

/// <summary>
/// « Utilisation IA » (#489, decision S): each adult sees their own calls; the month's totals, for the whole household,
/// are visible to both. Calls made by background work (no person) count only in the totals until their item is linked
/// (#167).
/// </summary>
public sealed record GetLlmUsageQuery(int? Page) : IQuery<LlmUsageView>;

[RequestHandler<GetLlmUsageQuery, LlmUsageView>]
public sealed class GetLlmUsageHandler(ILlmCallReads calls, IWorkerStore workers, IActorAccessor actors, IClock clock)
    : IRequestHandler<GetLlmUsageQuery, LlmUsageView>
{
    public async ValueTask<Result<LlmUsageView>> HandleAsync(GetLlmUsageQuery query, CancellationToken ct = default)
    {
        if (actors.Current?.PersonId is not { } person)
            return Result.Failure<LlmUsageView>(new UnauthorizedFailure("An adult reads their own calls.", null));

        var (page, skip) = ListPage.Of(query.Page, LlmUsageView.PageSize);
        var (items, total) = await calls.PageAsync(person.Value, skip, LlmUsageView.PageSize, ct);
        var month = BudgetMonths.Of(clock.GetCurrentInstant());
        var totals = await calls.MonthAsync(month.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
            month.PlusMonths(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(), ct);
        var names = (await workers.ListAsync(ct)).ToDictionary(w => w.Id, w => w.Name);
        return Result.Success(new LlmUsageView(
            [.. items.Select(c => new LlmCallView(c.Id, c.CreatedAt, c.Use, c.Model,
                c.Provider ?? (c.WorkerId is { } id ? names.GetValueOrDefault(id) : null),
                c.Provider is null && c.WorkerId is not null ? "worker" : c.Provider is null ? null : "cloud",
                c.TokensIn, c.TokensOut, c.CostEur is { } cost ? Eur(cost) : null, c.Status, c.Code))],
            total, page, month,
            new LlmMonthView(totals.Calls, totals.TokensIn, totals.TokensOut, Eur(totals.CostEur))));
    }

    private static string Eur(decimal amount) => Math.Round(amount, 4).ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture);
}
