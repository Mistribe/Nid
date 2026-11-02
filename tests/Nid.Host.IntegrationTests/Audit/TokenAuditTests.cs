using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.IntegrationTests.Audit;

/// <summary>Spec 4A §5: every audit writer names the token its actor used.</summary>
public class TokenAuditTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_write_and_a_security_event_made_with_a_token_carry_its_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var tokenId = Guid.CreateVersion7();
        var actor = Actor.Token(app.AdultA, tokenId,
            new HashSet<string> { ScopeNames.Read, ScopeNames.Write, ScopeNames.WriteDirect }, ActorOrigin.Api);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = actor;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new CreateDependentCommand("Kid"), TestContext.Current.CancellationToken)).IsSuccess);
            await scope.ServiceProvider.GetRequiredService<ISecurityEvents>().RecordAsync(
                SecurityEvent.Of(SecurityEventNames.TokenCreated, app.AdultA), TestContext.Current.CancellationToken);
        }

        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var write = await platform.AuditLog.SingleAsync(r => r.Category == "write" && r.Entity == "core.person", cancellationToken: TestContext.Current.CancellationToken);
        var created = await platform.AuditLog.SingleAsync(r => r.Action == SecurityEventNames.TokenCreated, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((tokenId, "api"), (write.TokenId!.Value, write.Via));
        Assert.Equal(tokenId, created.TokenId);
    }
}
