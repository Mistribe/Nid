using Nid.Core.Contracts;
using Nid.Goals.Domain;
using NodaTime;

namespace Nid.Goals.Tests;

public class GoalDeletionTests
{
    private static ItemAccess Item(string module, string kind, bool archived = false)
        => new(new ItemId(Guid.NewGuid()), "Item", Visibility.Shared, [])
        {
            Module = module, Kind = kind, ArchivedAt = archived ? Instant.FromUtc(2026, 1, 1, 0, 0) : null,
        };

    [Fact]
    public void An_envelope_is_the_investments_envelope_only()
    {
        Assert.True(GoalDeletion.IsEnvelope(Item("investments", "envelope")));
        Assert.False(GoalDeletion.IsEnvelope(Item("investments", "holding")));
        Assert.False(GoalDeletion.IsEnvelope(Item("banking", "envelope")));
        Assert.False(GoalDeletion.IsEnvelope(Item("goals", "goal")));
    }

    [Fact]
    public void A_goal_is_any_item_of_the_goals_module()
    {
        Assert.True(GoalDeletion.IsGoal(Item("goals", "goal")));
        Assert.False(GoalDeletion.IsGoal(Item("investments", "envelope")));
    }

    [Fact]
    public void Open_goals_block_and_closed_ones_release_their_funding()
    {
        var open = Item("goals", "goal");
        var closed = Item("goals", "goal", archived: true);

        Assert.Equal([open], GoalDeletion.Blocking([open, closed]));
        Assert.Equal([closed], GoalDeletion.Released([open, closed]));
        Assert.Empty(GoalDeletion.Blocking([closed]));
        Assert.Empty(GoalDeletion.Released([]));
    }
}
