using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Audit;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Audit;

/// <summary>2A spec §6 and §8: a fact's audit, and the caller's unit of work, through the host's own wiring.</summary>
[Collection("postgres")]
public class FactAuditTests(PostgresFixture fixture)
{
    private static readonly LocalDate Day = new(2025, 10, 1);

    private static async Task<List<AuditEntryRow>> WritesAsync(NidApp app, string entity)
    {
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        return await platform.AuditLog.Where(r => r.Category == "write" && r.Entity == entity).OrderBy(r => r.Seq).ToListAsync();
    }

    private static async Task<ItemId> EnvelopeAsync(IServiceProvider services, PersonId owner)
        => (await services.GetRequiredService<IItemRegistry>()
            .CreateAsync(new NewItem("test", "envelope", "PEA", Visibility.Private, "EUR", [new OwnerShare(owner, 10_000)]), default)).Ok();

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Ok();

    [Fact]
    public async Task A_correction_is_audited_under_the_item_with_the_old_and_new_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ItemId item;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            item = await EnvelopeAsync(scope.ServiceProvider, app.AdultA);
            var valuations = scope.ServiceProvider.GetRequiredService<IValuations>();
            (await valuations.RecordAsync(new NewValuation(item, Day, Eur(100m), null), default)).Ok();
            (await valuations.RecordAsync(new NewValuation(item, Day, Eur(110m), null), default)).Ok();
        }

        var entries = await WritesAsync(app, "core.valuation");
        Assert.Equal(["insert", "update", "insert"], entries.Select(e => e.Action));
        Assert.All(entries, e =>
        {
            Assert.Equal([item.Value], e.ItemIds);
            Assert.Equal(("ui", app.AdultA.Value), (e.Via, e.PersonId!.Value));
        });
        using var retired = JsonDocument.Parse(entries[1].Data);
        Assert.Equal(["retired_at"], retired.RootElement.GetProperty("after").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_rolled_back_unit_of_work_leaves_no_fact_and_no_audit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            var item = await EnvelopeAsync(scope.ServiceProvider, app.AdultA);
            await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(default);
            (await scope.ServiceProvider.GetRequiredService<IFlows>()
                .RecordAsync(new NewFlow(item, Day, FlowKind.Contribution, Eur(50m), null, null, null), default)).Ok();
            // disposed without CommitAsync: rolled back
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(0, await core.Flows.CountAsync());
        Assert.Empty(await WritesAsync(app, "core.flow"));
    }

    [Fact]
    public async Task A_conflict_inside_a_unit_of_work_is_a_failure_not_an_exception()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ItemId item;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            item = await EnvelopeAsync(scope.ServiceProvider, app.AdultA);
            (await scope.ServiceProvider.GetRequiredService<IFlows>()
                .RecordAsync(new NewFlow(item, Day, FlowKind.Opening, Eur(1_000m), null, null, null), default)).Ok();
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(default);
            var second = await scope.ServiceProvider.GetRequiredService<IFlows>()
                .RecordAsync(new NewFlow(item, Day, FlowKind.Opening, Eur(2_000m), null, null, null), default);

            Assert.True(second.TryGetFailure(out var failure));
            Assert.Equal(CoreFactCodes.OpeningExists, failure!.Code);
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.Equal(1, await core.Flows.CountAsync());
    }

    [Fact]
    public async Task The_host_resolves_the_three_contracts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IValuations>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IFlows>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IHouseholdCalendar>());
    }
}
