using System.Globalization;
using Nid.Core.Contracts;
using Nid.Goals.Contracts;
using UnambitiousFx.Functional;

namespace Nid.Goals.Application;

/// <summary>Issue #81: the goals' list read (<see cref="ListGoalsHandler"/>'s) in the reader's <c>Me</c> scope, open goals only.</summary>
public sealed class GoalsEmergencyBuffer(IVisibleItems visible, IItemRegistry items, IGoalStore goals, GoalReader reader) : IEmergencyBuffer
{
    public async Task<Result<EmergencyBuffer>> ReadAsync(PersonId person, int months, CancellationToken ct)
    {
        var ids = await visible.ForAsync(person, VisibilityScope.Me, ct);
        var found = await goals.FindManyAsync(ids.ToList(), ct);
        var registered = await items.FindManyAsync(found.Select(g => g.ItemId).ToList(), ct);
        var read = await reader.ReadAsync(person, VisibilityScope.Me,
            found.Where(g => registered.ContainsKey(g.ItemId)).Select(g => (g, registered[g.ItemId])).ToList(), ct);
        if (!read.TryGetValue(out var details))
            return Result.Failure<EmergencyBuffer>(read.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        var behind = details!.Any(d => d.Goal.Status == GoalStatuses.Behind);
        var fund = details!.Where(d => d.Goal.Kind == GoalKinds.EmergencyFund && d.Goal.Target is not null)
            .Select(d => (decimal?)decimal.Parse(d.Goal.Target!.Amount, CultureInfo.InvariantCulture)).Max();
        if (fund is not null)
            return Result.Success(new EmergencyBuffer(fund, behind));

        var target = await reader.EmergencyTargetAsync(person, VisibilityScope.Me, months, ct);
        if (!target.TryGetValue(out var view))
            return Result.Failure<EmergencyBuffer>(target.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        return Result.Success(new EmergencyBuffer(view!.Target is { } t ? decimal.Parse(t.Amount, CultureInfo.InvariantCulture) : null, behind));
    }
}
