using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.IntegrationTests.Proposals;

/// <summary>Spec 4D1 §5: a document written directly — validated and applied in one unit of work, no proposal row.</summary>
public class DirectWriteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    internal static readonly HashSet<string> Direct = [ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect];

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => NidApp.StartAsync(fixture, services => services.AddScoped<IProposalHandler, EchoProposalHandler>());

    private static ApplyDirectCommand Echo(ItemId item, object payload)
        => new(EchoProposalHandler.KindName, item, JsonSerializer.SerializeToElement(payload));

    /// <summary>As a token holding <paramref name="scopes"/>, through the real pipeline.</summary>
    internal static async Task<Result<DirectWrite>> AsTokenAsync(NidApp app, IReadOnlySet<string> scopes, ApplyDirectCommand command)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Token(app.AdultA, Guid.NewGuid(), scopes, ActorOrigin.Mcp);
        return await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(command);
    }

    private static async Task<bool> AnyProposalAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        return await core.Proposals.AnyAsync();
    }

    [Fact]
    public async Task A_direct_write_validates_applies_and_stores_no_proposal()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var written = await AsTokenAsync(app, Direct, Echo(item, new { subject = "s1" }));

        Assert.True(written.TryGetValue(out var write));
        Assert.Equal("applied", write!.Outcome);
        Assert.Single(write.AppliedRefs);
        Assert.Empty(write.Skipped);
        Assert.False(await AnyProposalAsync(app));
    }

    [Fact]
    public async Task Without_write_direct_a_direct_write_is_refused()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var refused = await AsTokenAsync(app, new HashSet<string> { ScopeNames.Read, ScopeNames.Write }, Echo(item, new { subject = "s1" }));

        Assert.True(refused.TryGetFailure(out var failure));
        Assert.IsType<UnauthorizedFailure>(failure);
        Assert.Contains("write:direct", failure!.Message);
    }

    [Fact]
    public async Task A_refusal_or_a_failed_application_answers_its_code()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        foreach (var (payload, code) in new (object Payload, string Code)[]
                 {
                     (new { subject = "s1", refuse = true }, "test.refused"),
                     (new { subject = "s1", applyFails = true }, "test.apply_failed"),
                 })
        {
            var result = await AsTokenAsync(app, Direct, Echo(item, payload));
            Assert.True(result.TryGetFailure(out var failure));
            Assert.Equal(code, failure!.Code);
        }
        var unknown = await AsTokenAsync(app, Direct, new ApplyDirectCommand("nope", item, JsonSerializer.SerializeToElement(new { })));
        Assert.True(unknown.TryGetFailure(out var kind));
        Assert.Equal(ProposalCodes.KindUnknown, kind!.Code);
        Assert.False(await AnyProposalAsync(app));
    }

    /// <summary>Task 1's review: a failed application is never committed, so a later write in the same app starts clean.</summary>
    [Fact]
    public async Task A_direct_write_whose_application_fails_leaves_nothing_stored()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var failed = await AsTokenAsync(app, Direct, Echo(item, new { subject = "s1", applyFails = true }));

        Assert.True(failed.TryGetFailure(out var failure));
        Assert.Equal("test.apply_failed", failure!.Code);
        Assert.False(await AnyProposalAsync(app));
        Assert.True((await AsTokenAsync(app, Direct, Echo(item, new { subject = "s1" }))).IsSuccess);
    }

    [Fact]
    public async Task A_conforme_document_is_answered_and_nothing_is_written()
    {
        await using var app = await StartAsync(fixture);
        var item = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        var result = await AsTokenAsync(app, Direct, Echo(item, new { subject = "s1", conforme = true }));

        Assert.True(result.TryGetValue(out var write));
        Assert.Equal("conforme", write!.Outcome);
        Assert.Empty(write.AppliedRefs);
        Assert.Equal("s1", write.Conforme!.Value.GetProperty("given").GetProperty("subject").GetString());
        Assert.False(await AnyProposalAsync(app));
    }
}
