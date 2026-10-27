using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Application.Fx;
using Nid.Core.Contracts;
using Nid.Host;
using Nid.Platform;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using Nid.TestSupport;
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
        await using var scope = provider.CreateAsyncScope();

        Assert.Equal(
            [typeof(TraceBehavior<,>), typeof(ActorBehavior<,>), typeof(AuditBehavior<,>), typeof(AdminBehavior<,>), typeof(ScopeBehavior<,>), typeof(ItemAccessBehavior<,>), typeof(DocumentMaskBehavior<,>), typeof(OutputGuardBehavior<,>)],
            ChainOf<ProvisionAdultCommand, PersonInfo>(scope));
        Assert.Equal(
            [typeof(TraceBehavior<,>), typeof(ActorBehavior<,>), typeof(AuditBehavior<,>), typeof(AdminBehavior<,>), typeof(ScopeBehavior<,>), typeof(ItemAccessBehavior<,>), typeof(DocumentMaskBehavior<,>), typeof(OutputGuardBehavior<,>)],
            ChainOf<CreateDependentCommand, PersonInfo>(scope));
        Assert.Equal(
            [typeof(TraceBehavior<,>), typeof(ActorBehavior<,>), typeof(AuditBehavior<,>), typeof(AdminBehavior<,>), typeof(ScopeBehavior<,>), typeof(ItemAccessBehavior<,>), typeof(DocumentMaskBehavior<,>), typeof(OutputGuardBehavior<,>)],
            ChainOf<ListPersonsQuery, IReadOnlyList<PersonInfo>>(scope));
    }

    private static async Task<UnambitiousFx.Functional.Failures.Failure?> RunAsync(IServiceScope scope, Actor actor, object request)
    {
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = actor;
        var invoker = scope.ServiceProvider.GetRequiredService<IInvoker>();
        var result = request switch
        {
            GetFxSyncRunsQuery q => (await invoker.InvokeAsync(q)).TryGetFailure(out var f1) ? f1 : null,
            RequestFxSyncCommand c => (await invoker.InvokeAsync(c)).TryGetFailure(out var f2) ? f2 : null,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return result;
    }

    [Fact]
    public async Task An_admin_only_request_is_refused_to_an_adult_who_is_not_admin()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();

        var failure = await RunAsync(scope, Actor.Browser(PersonId.New()), new RequestFxSyncCommand());

        Assert.Equal(AdminCodes.Required, failure?.Code);
    }

    [Fact]
    public async Task An_admin_and_the_system_pass_the_admin_stage()
    {
        await using var provider = await ProviderAsync();

        await using (var scope = provider.CreateAsyncScope())
            Assert.Null(await RunAsync(scope, Actor.Browser(PersonId.New(), isAdmin: true), new GetFxSyncRunsQuery()));
        await using (var scope = provider.CreateAsyncScope())
            Assert.Null(await RunAsync(scope, Actor.System, new GetFxSyncRunsQuery()));
    }

    [Fact]
    public async Task An_actor_built_for_a_token_is_never_admin()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var token = new Actor(ActorKind.Person, PersonId.New(), ActorOrigin.Mcp,
            new HashSet<string> { ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect });

        Assert.Equal(AdminCodes.Required, (await RunAsync(scope, token, new GetFxSyncRunsQuery()))?.Code);
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
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new ListPersonsQuery());

        Assert.True(result.TryGetFailure(out var failure));
        Assert.IsType<UnauthenticatedFailure>(failure);
    }

    [Fact]
    public async Task A_command_requires_write_direct_and_write_alone_is_refused()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current =
            Actor.Token(PersonId.New(), Guid.NewGuid(), new HashSet<string> { ScopeNames.Read, ScopeNames.Write }, ActorOrigin.Api);

        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new CreateDependentCommand("Kid"));

        Assert.True(result.TryGetFailure(out var failure));
        Assert.IsType<UnauthorizedFailure>(failure);
        Assert.Contains("write:direct", failure!.Message);
    }

    [Fact]
    public async Task A_read_only_actor_cannot_run_a_command()
    {
        await using var provider = await ProviderAsync();
        await using var scope = provider.CreateAsyncScope();
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
        await using var scope = provider.CreateAsyncScope();
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

    [Fact]
    public async Task A_browser_only_request_is_refused_to_a_token_and_runs_for_a_session()
    {
        await using var provider = await ProviderAsync();
        var person = PersonId.New();

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current =
                Actor.Token(person, Guid.NewGuid(), new HashSet<string> { ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect }, ActorOrigin.Api);
            var refused = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new RevokeMySessionsCommand());
            Assert.True(refused.TryGetFailure(out var failure));
            Assert.Equal(BrowserOnlyCodes.Required, failure!.Code);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(person);
            var ran = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new RevokeMySessionsCommand());
            // The person does not exist in this database: the handler ran and answered not found, past the scope stage.
            Assert.True(ran.TryGetFailure(out var notFound));
            Assert.NotEqual(BrowserOnlyCodes.Required, notFound!.Code);
        }
    }
}
