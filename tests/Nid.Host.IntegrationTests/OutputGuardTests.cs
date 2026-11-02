using Nid.Core.Contracts;
using Nid.Platform;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests;

public class OutputGuardTests
{
    private sealed record LeakyResponse(IReadOnlyList<ItemId> Items) : IExposesItems
    {
        public IEnumerable<ItemId> ExposedItems() => Items;
    }

    private sealed record HarmlessResponse(string Text);

    private sealed record AnyQuery : IQuery<LeakyResponse>;

    private sealed record HarmlessQuery : IQuery<HarmlessResponse>;

    private sealed class FixedVisibleItems(params ItemId[] visible) : IVisibleItems
    {
        Task<IReadOnlySet<ItemId>> IVisibleItems.ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
            => ForAsync(actor, scope, includeArchived: false, ct);

        public Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<ItemId>>(visible.ToHashSet());
    }

    private sealed class Accessor(PersonId person) : IActorAccessor
    {
        public Actor? Current { get; set; } = Actor.Browser(person);
    }

    private sealed class SystemAccessor : IActorAccessor
    {
        public Actor? Current { get; set; } = Actor.System;
    }

    private sealed class ThrowingVisibleItems : IVisibleItems
    {
        Task<IReadOnlySet<ItemId>> IVisibleItems.ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
            => ForAsync(actor, scope, includeArchived: false, ct);

        public Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct)
            => throw new InvalidOperationException("the database is unreachable");
    }

    [Fact]
    public async Task A_response_exposing_an_invisible_item_is_refused_and_carries_no_data()
    {
        var visible = new ItemId(Guid.CreateVersion7());
        var leaked = new ItemId(Guid.CreateVersion7());
        var guard = new OutputGuardBehavior<AnyQuery, LeakyResponse>(
            new Accessor(new PersonId(Guid.CreateVersion7())), new FixedVisibleItems(visible), NullViolations.Instance);

        var result = await guard.HandleAsync(new AnyQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new LeakyResponse([visible, leaked]))), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("core.access_violation", result.FailureCode());
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public async Task A_response_within_the_visible_set_passes_untouched()
    {
        var visible = new ItemId(Guid.CreateVersion7());
        var guard = new OutputGuardBehavior<AnyQuery, LeakyResponse>(
            new Accessor(new PersonId(Guid.CreateVersion7())), new FixedVisibleItems(visible), NullViolations.Instance);

        var result = await guard.HandleAsync(new AnyQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new LeakyResponse([visible]))), TestContext.Current.CancellationToken);

        Assert.Equal([visible], result.Ok().Items);
    }

    [Fact]
    public async Task A_response_exposing_no_items_is_not_inspected()
    {
        var guard = new OutputGuardBehavior<HarmlessQuery, HarmlessResponse>(
            new Accessor(new PersonId(Guid.CreateVersion7())), new ThrowingVisibleItems(), NullViolations.Instance);

        var result = await guard.HandleAsync(new HarmlessQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new HarmlessResponse("nothing item-scoped"))), TestContext.Current.CancellationToken);

        Assert.Equal("nothing item-scoped", result.Ok().Text);
    }

    [Fact]
    public async Task The_guard_fails_closed_when_it_cannot_determine_what_is_visible()
    {
        var guard = new OutputGuardBehavior<AnyQuery, LeakyResponse>(
            new Accessor(new PersonId(Guid.CreateVersion7())), new ThrowingVisibleItems(), NullViolations.Instance);

        var result = await guard.HandleAsync(new AnyQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new LeakyResponse([new ItemId(Guid.CreateVersion7())]))), TestContext.Current.CancellationToken);

        // An exception inside the guard is a refusal, never a pass.
        Assert.Equal("core.access_violation", result.FailureCode());
    }

    [Fact]
    public async Task A_violation_is_recorded()
    {
        var recorder = new RecordingViolations();
        var guard = new OutputGuardBehavior<AnyQuery, LeakyResponse>(
            new Accessor(new PersonId(Guid.CreateVersion7())), new FixedVisibleItems(), recorder);

        await guard.HandleAsync(new AnyQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new LeakyResponse([new ItemId(Guid.CreateVersion7())]))), TestContext.Current.CancellationToken);

        Assert.Single(recorder.Recorded);
    }

    [Fact]
    public async Task A_system_actor_response_exposing_an_item_is_refused_and_carries_no_data()
    {
        var guard = new OutputGuardBehavior<AnyQuery, LeakyResponse>(
            new SystemAccessor(), new ThrowingVisibleItems(), NullViolations.Instance);

        var result = await guard.HandleAsync(new AnyQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new LeakyResponse([new ItemId(Guid.CreateVersion7())]))), TestContext.Current.CancellationToken);

        // A system actor has no PersonId and so no visible-item set to prove the response legitimate:
        // uncertainty is a refusal here too, never a silent pass.
        Assert.Equal("core.access_violation", result.FailureCode());
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public async Task A_system_actor_response_exposing_nothing_passes_untouched()
    {
        var guard = new OutputGuardBehavior<HarmlessQuery, HarmlessResponse>(
            new SystemAccessor(), new ThrowingVisibleItems(), NullViolations.Instance);

        var result = await guard.HandleAsync(new HarmlessQuery(),
            (_, _) => ValueTask.FromResult(Result.Success(new HarmlessResponse("nothing item-scoped"))), TestContext.Current.CancellationToken);

        Assert.Equal("nothing item-scoped", result.Ok().Text);
    }

    private sealed class RecordingViolations : IAccessViolations
    {
        public List<string> Recorded { get; } = [];

        public ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, CancellationToken ct)
        {
            Recorded.Add(request);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullViolations : IAccessViolations
    {
        public static NullViolations Instance { get; } = new();

        public ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
