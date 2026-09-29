using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Audit;
using Nid.TestSupport;
using NodaTime;
using Npgsql;

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

    /// <summary>
    /// Makes every insert into <paramref name="table"/> fail with <paramref name="sqlState"/>, updates untouched: the only
    /// deterministic way to make a supersede fail after its retirement was saved, since the store's own checks see
    /// every conflicting row the transaction can see and refuse before any save.
    /// </summary>
    private static async Task RefuseInsertsAsync(NidApp app, string table, string sqlState)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            create function core.test_refuse_{table}_insert() returns trigger language plpgsql as
            $$ begin raise exception 'insert refused by the test' using errcode = '{sqlState}'; end $$;
            create trigger test_refuse_insert before insert on core.{table}
            for each row execute function core.test_refuse_{table}_insert();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static bool TracksFacts(IServiceProvider services)
        => services.GetRequiredService<CoreDbContext>().ChangeTracker.Entries().Any(e => e.Entity is FlowRow or ValuationRow);

    [Fact]
    public async Task A_supersede_whose_insert_conflicts_leaves_nothing_in_the_unit_of_work()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ItemId item;
        Guid opening;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            item = await EnvelopeAsync(scope.ServiceProvider, app.AdultA);
            opening = (await scope.ServiceProvider.GetRequiredService<IFlows>()
                .RecordAsync(new NewFlow(item, Day, FlowKind.Opening, Eur(1_000m), null, null, null), default)).Ok().Id;
        }

        await RefuseInsertsAsync(app, "flow", "23505"); // unique_violation: the path a lost race takes

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(default);
            var valuations = scope.ServiceProvider.GetRequiredService<IValuations>();
            Assert.True((await valuations.RecordAsync(new NewValuation(item, Day, Eur(900m), null), default)).IsSuccess);

            var superseded = await scope.ServiceProvider.GetRequiredService<IFlows>()
                .SupersedeAsync(item, opening, new NewFlow(item, Day, FlowKind.Opening, Eur(1_200m), null, null, null), default);

            Assert.True(superseded.TryGetFailure(out var failure));
            Assert.Equal(CoreFactCodes.FactConflict, failure!.Code);
            Assert.False(TracksFacts(scope.ServiceProvider));
            await transaction.CommitAsync(default); // the caller keeps its own work: its choice
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var flow = Assert.Single(await core.Flows.ToListAsync());
        Assert.Equal((opening, (Instant?)null), (flow.Id, flow.RetiredAt));
        Assert.Equal(["insert"], (await WritesAsync(app, "core.flow")).Select(e => e.Action));
        Assert.Equal(1, await core.Valuations.CountAsync(v => v.RetiredAt == null));
        Assert.Equal(["insert"], (await WritesAsync(app, "core.valuation")).Select(e => e.Action));
    }

    [Fact]
    public async Task A_same_date_value_whose_insert_throws_leaves_nothing_in_the_unit_of_work()
    {
        await using var app = await NidApp.StartAsync(fixture);
        ItemId item;
        Guid first;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            item = await EnvelopeAsync(scope.ServiceProvider, app.AdultA);
            first = (await scope.ServiceProvider.GetRequiredService<IValuations>()
                .RecordAsync(new NewValuation(item, Day, Eur(100m), null), default)).Ok().Id;
        }

        await RefuseInsertsAsync(app, "valuation", "P0001"); // raise_exception: not a conflict, so the store throws

        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
            await using var transaction = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(default);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IFlows>()
                .RecordAsync(new NewFlow(item, Day, FlowKind.Contribution, Eur(50m), null, null, null), default)).IsSuccess);

            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IValuations>()
                .RecordAsync(new NewValuation(item, Day, Eur(110m), null), default));

            Assert.False(TracksFacts(scope.ServiceProvider));
            await transaction.CommitAsync(default);
        }

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        var value = Assert.Single(await core.Valuations.ToListAsync());
        Assert.Equal((first, (Instant?)null), (value.Id, value.RetiredAt));
        Assert.Equal(["insert"], (await WritesAsync(app, "core.valuation")).Select(e => e.Action));
        Assert.Equal(1, await core.Flows.CountAsync());
        Assert.Equal(["insert"], (await WritesAsync(app, "core.flow")).Select(e => e.Action));
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
