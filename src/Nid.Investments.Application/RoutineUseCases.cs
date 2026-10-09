using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

/// <summary>2E2 spec §4: the month's envelopes for the routine, as of <c>On</c> (default: the end of the previous month).</summary>
public sealed record GetRoutineQuery(LocalDate? On) : IQuery<RoutineView>;

public sealed record RoutineLineView(ItemId Id, string Name, string Currency, string Cadence, AmountView? LastValue, LocalDate? LastAsOf, bool Due);

public sealed record RoutineView(LocalDate On, IReadOnlyList<RoutineLineView> Lines) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Lines.Select(l => l.Id);
}

internal static class Routine
{
    public static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    public static Failure Field(string code, string message, string field)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });
}

[RequestHandler<GetRoutineQuery, RoutineView>]
public sealed class GetRoutineHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IAccessPolicy policy, IActorAccessor actor,
    IHouseholdCalendar calendar)
    : IRequestHandler<GetRoutineQuery, RoutineView>
{
    public async ValueTask<Result<RoutineView>> HandleAsync(GetRoutineQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        var on = query.On ?? RoutineMath.DefaultDate(today);
        if (on > today)
            return Result.Failure<RoutineView>(Routine.Field(CoreFactCodes.DateInFuture, "The routine's date cannot be after today.", "on"));

        var lines = new List<RoutineLineView>();
        foreach (var envelope in await envelopes.FindManyAsync((await visible.ForAsync(reader, VisibilityScope.Me, ct)).ToList(), ct))
        {
            if (!(await policy.CheckAsync(reader, envelope.ItemId, AccessKind.Write, ct)).IsSuccess) continue;
            if (!(await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out var f) || f!.Item.ArchivedAt is not null) continue;
            var last = f.Values.Where(v => v.AsOf <= on).OrderByDescending(v => v.AsOf).FirstOrDefault();
            lines.Add(new RoutineLineView(envelope.ItemId, f.Item.DisplayName, f.Currency.Code, EnvelopeWire.Of(envelope.Cadence),
                last is null ? null : AmountView.From(last.Value), last?.AsOf,
                RoutineMath.IsDue(envelope.Cadence, f.Values.Select(v => v.AsOf), on)));
        }
        return Result.Success(new RoutineView(on, lines
            .OrderBy(l => l.Due ? 0 : 1).ThenBy(l => l.Name, Routine.French).ThenBy(l => l.Id.Value).ToList()));
    }
}
