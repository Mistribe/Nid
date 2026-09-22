using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Host;
using Nid.Platform;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class PipelineTests(PostgresFixture postgres)
{
    private async Task<ServiceProvider> ProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddNid(await postgres.NewConnectionStringAsync());
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// A behavior registered in the wrong place is skipped without any error, so every request of the
    /// module must be checked to traverse the whole chain, in order.
    /// </summary>
    [Fact]
    public async Task Every_core_request_traverses_the_full_behavior_chain_in_order()
    {
        await using var provider = await ProviderAsync();
        using var scope = provider.CreateScope();

        Assert.Equal(
            [typeof(TraceBehavior<,>), typeof(ActorBehavior<,>), typeof(ScopeBehavior<,>), typeof(ItemAccessBehavior<,>)],
            ChainOf<ProvisionAdultCommand, PersonInfo>(scope));
        Assert.Equal(
            [typeof(TraceBehavior<,>), typeof(ActorBehavior<,>), typeof(ScopeBehavior<,>), typeof(ItemAccessBehavior<,>)],
            ChainOf<CreateDependentCommand, PersonInfo>(scope));
        Assert.Equal(
            [typeof(TraceBehavior<,>), typeof(ActorBehavior<,>), typeof(ScopeBehavior<,>), typeof(ItemAccessBehavior<,>)],
            ChainOf<ListPersonsQuery, IReadOnlyList<PersonInfo>>(scope));
    }

    private static Type[] ChainOf<TRequest, TResponse>(IServiceScope scope)
        where TRequest : IRequest<TResponse>
        where TResponse : notnull
        => scope.ServiceProvider.GetServices<IRequestPipelineBehavior<TRequest, TResponse>>()
            .OfType<IOrderedPipelineBehavior>()
            .OrderBy(b => b.Order)
            .Select(b => b.GetType().GetGenericTypeDefinition())
            .ToArray();

    [Fact]
    public async Task A_request_without_actor_is_unauthenticated()
    {
        await using var provider = await ProviderAsync();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new ListPersonsQuery());

        Assert.True(result.TryGetFailure(out var failure));
        Assert.IsType<UnauthenticatedFailure>(failure);
    }

    [Fact]
    public async Task A_read_only_actor_cannot_run_a_command()
    {
        await using var provider = await ProviderAsync();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current =
            new Actor(ActorKind.Person, PersonId.New(), ActorOrigin.Mcp, new HashSet<string> { ScopeNames.Read });

        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new CreateDependentCommand("Kid"));

        Assert.True(result.TryGetFailure(out var failure));
        Assert.IsType<UnauthorizedFailure>(failure);
    }

    [Fact]
    public async Task System_actor_provisions_adults_and_an_adult_creates_a_dependent()
    {
        await using var provider = await ProviderAsync();
        using var scope = provider.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IInvoker>();
        var accessor = scope.ServiceProvider.GetRequiredService<IActorAccessor>();

        accessor.Current = Actor.System;
        var adult = await invoker.InvokeAsync(new ProvisionAdultCommand("sub-a", "Alice", null));
        Assert.True(adult.TryGetValue(out var alice));

        accessor.Current = Actor.Browser(alice.Id);
        var kid = await invoker.InvokeAsync(new CreateDependentCommand("Kid"));
        Assert.True(kid.TryGetValue(out var child));
        Assert.Equal(PersonKind.Dependent, child.Kind);

        var list = await invoker.InvokeAsync(new ListPersonsQuery());
        Assert.True(list.TryGetValue(out var persons));
        Assert.Equal(2, persons.Count);
    }
}
