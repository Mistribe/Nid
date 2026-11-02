using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Childcare.IntegrationTests;

/// <summary>Childcare spec §1.8–11: the arrangement, its dated terms and its weeks without care, stored.</summary>
public class ChildcareStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly IReadOnlyList<WeekdaySlot> Schedule =
        [.. new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
            .Select(d => new WeekdaySlot(d, new LocalTime(8, 0), new LocalTime(17, 30), true))];

    private static Terms TermsOf(ItemId id, LocalDate from, decimal rate = 4m)
        => new(id, from, null, rate, null, 0.10m, 3.80m, 3.20m, [new WeekType("Semaine type", 46, Schedule)]);

    private static async Task<T> WithStoreAsync<T>(NidApp app, Func<IChildcareStore, Task<T>> act)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return await act(scope.ServiceProvider.GetRequiredService<IChildcareStore>());
    }

    private static async Task<ItemId> CreatedAsync(NidApp app)
    {
        var id = new ItemId(Guid.NewGuid());
        await WithStoreAsync(app, async s =>
        {
            await s.CreateAsync(new Arrangement(id, app.Dependent, "Martine", "P123", null, 0), TermsOf(id, new LocalDate(2025, 9, 1)), default);
            return 0;
        });
        return id;
    }

    [Fact]
    public async Task Create_then_read_terms_and_schedule()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        var arrangement = await WithStoreAsync(app, s => s.FindAsync(id, default));
        Assert.Equal("Martine", arrangement!.CarerName);
        Assert.Equal(app.Dependent, arrangement.Child);
        var terms = Assert.Single(await WithStoreAsync(app, s => s.TermsOfAsync([id], default)));
        Assert.Equal(46, terms.ProgrammedWeeks);
        var type = Assert.Single(terms.WeekTypes);
        Assert.Equal(4, type.Schedule.Count);
        Assert.Equal(new LocalTime(17, 30), type.Schedule[0].Departure);
        Assert.Null(terms.To);
    }

    [Fact]
    public async Task Add_terms_closes_the_previous_the_day_before()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        await WithStoreAsync(app, async s => { await s.AddTermsAsync(TermsOf(id, new LocalDate(2026, 1, 1), 4.2m), default); return 0; });
        var terms = await WithStoreAsync(app, s => s.TermsOfAsync([id], default));
        Assert.Equal(2, terms.Count);
        Assert.Equal(new LocalDate(2025, 12, 31), terms[0].To);
        Assert.Equal(4.2m, terms[1].NetHourlyRate);
        Assert.Equal(4, terms[1].WeekTypes[0].Schedule.Count);
    }

    private static readonly IReadOnlyList<WeekdaySlot> Short = [Schedule[0] with { Departure = new LocalTime(16, 45) }];

    private static Terms TwoTypes(ItemId id, LocalDate from)
        => TermsOf(id, from) with { WeekTypes = [new WeekType("Longue", 23, Schedule), new WeekType("Courte", 22, Short)] };

    [Fact]
    public async Task Two_week_types_round_trip_in_order_and_replace_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        var from = new LocalDate(2025, 9, 1);
        await WithStoreAsync(app, async s => { await s.ReplaceCurrentTermsAsync(TwoTypes(id, from), default); return 0; });
        var terms = Assert.Single(await WithStoreAsync(app, s => s.TermsOfAsync([id], default)));
        Assert.Equal(45, terms.ProgrammedWeeks);
        Assert.Equal(["Longue", "Courte"], terms.WeekTypes.Select(w => w.Name));
        Assert.Equal([23, 22], terms.WeekTypes.Select(w => w.Weeks));
        Assert.Equal(4, terms.WeekTypes[0].Schedule.Count);
        Assert.Equal(new LocalTime(16, 45), Assert.Single(terms.WeekTypes[1].Schedule).Departure);

        await WithStoreAsync(app, async s => { await s.ReplaceCurrentTermsAsync(TermsOf(id, from), default); return 0; });
        Assert.Single(Assert.Single(await WithStoreAsync(app, s => s.TermsOfAsync([id], default))).WeekTypes);
    }

    [Fact]
    public async Task Overrides_are_set_replaced_and_removed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        var (first, second) = (new LocalDate(2025, 9, 8), new LocalDate(2025, 9, 1));
        await WithStoreAsync(app, async s => { await s.SetOverrideAsync(id, first, 1, default); await s.SetOverrideAsync(id, second, 0, default); return 0; });
        await WithStoreAsync(app, async s => { await s.SetOverrideAsync(id, first, 0, default); return 0; });
        Assert.Equal([(id, new WeekOverride(second, 0)), (id, new WeekOverride(first, 0))], await WithStoreAsync(app, s => s.OverridesOfAsync([id], default)));

        await WithStoreAsync(app, async s => { await s.SetOverrideAsync(id, first, null, default); await s.SetOverrideAsync(id, new LocalDate(2025, 12, 1), null, default); return 0; });
        Assert.Equal([(id, new WeekOverride(second, 0))], await WithStoreAsync(app, s => s.OverridesOfAsync([id], default)));
    }

    [Fact]
    public async Task Migration_wraps_an_existing_revision_in_one_week_type()
    {
        var connectionString = await fixture.NewEmptyConnectionStringAsync();
        await using var db = PostgresFixture.NewChildcareContext(connectionString);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261031064823_ChildcareStatements", TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            insert into childcare.arrangement (item_id, child_id, carer_name) values ('01920000-0000-7000-8000-0000000000a1', '01920000-0000-7000-8000-0000000000b1', 'Martine');
            insert into childcare.terms (item_id, "from", net_hourly_rate, overtime_rate, upkeep_per9h, programmed_weeks)
              values ('01920000-0000-7000-8000-0000000000a1', '2025-09-01', 4.30, 0.1, 3.80, 46);
            insert into childcare.weekday (item_id, terms_from, day, arrival, departure, meal) values
              ('01920000-0000-7000-8000-0000000000a1', '2025-09-01', 1, '08:00', '17:30', true),
              ('01920000-0000-7000-8000-0000000000a1', '2025-09-01', 4, '08:00', '16:45', false);
            """, TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var id = new ItemId(Guid.Parse("01920000-0000-7000-8000-0000000000a1"));
        var terms = Assert.Single(await new Nid.Childcare.Infrastructure.Persistence.ChildcareStore(db, NodaTime.SystemClock.Instance).TermsOfAsync([id], TestContext.Current.CancellationToken));
        var type = Assert.Single(terms.WeekTypes);
        Assert.Equal(("Semaine type", 46), (type.Name, type.Weeks));
        Assert.Equal([IsoDayOfWeek.Monday, IsoDayOfWeek.Thursday], type.Schedule.Select(s => s.Day));
        Assert.Equal(new LocalTime(16, 45), type.Schedule[1].Departure);
    }

    [Fact]
    public async Task Replace_gaps()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        await WithStoreAsync(app, async s => { await s.ReplaceGapsAsync(id, [new CareGap(new LocalDate(2026, 2, 16), 2)], default); return 0; });
        await WithStoreAsync(app, async s => { await s.ReplaceGapsAsync(id, [new CareGap(new LocalDate(2026, 7, 27), 4)], default); return 0; });
        var (item, gap) = Assert.Single(await WithStoreAsync(app, s => s.GapsOfAsync([id], default)));
        Assert.Equal(id, item);
        Assert.Equal(new CareGap(new LocalDate(2026, 7, 27), 4), gap);
    }

    [Fact]
    public async Task Stale_version_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        var read = (await WithStoreAsync(app, s => s.FindAsync(id, default)))!;
        Assert.True((await WithStoreAsync(app, s => s.UpdateAsync(read with { CarerName = "Martine D." }, default))).IsSuccess);
        var stale = await WithStoreAsync(app, s => s.UpdateAsync(read with { CarerName = "Autre" }, default));
        Assert.True(stale.TryGetFailure(out var failure));
        Assert.Equal(ChildcareCodes.Conflict, failure!.Code);
    }

    private static readonly YearMonth September = new(2025, 9);

    private static Statement DraftOf(ItemId id, uint version = 0)
    {
        var days = Enumerable.Range(1, 30).Select(d => new LocalDate(2025, 9, d))
            .Select(d => d.Day == 8
                ? new StatementDay(d, true, false, null, null, false, AbsenceReason.ChildSick)
                : Schedule.Any(s => s.Day == d.DayOfWeek)
                    ? new StatementDay(d, true, true, new LocalTime(8, 0), new LocalTime(17, 30), true, null)
                    : new StatementDay(d, false, false, null, null, false, null))
            .ToList();
        return new Statement(id, September, false, 12.5m, null, days, null, version);
    }

    private static readonly MonthFigures Figures = new(
        new PajemploiDeclaration(146, 15, null, 1.5m, 0m, 598.02m, 70.40m, 59.50m, 12.5m),
        626.37m, 32.37m, 6m, 0m, 100m, 598.70m, 526.70m, 1);

    [Fact]
    public async Task Draft_round_trips_its_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        Assert.True((await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id), default))).IsSuccess);
        var read = await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default));
        Assert.False(read!.Validated);
        Assert.Equal(12.5m, read.Mileage);
        Assert.Equal(DraftOf(id).Days, read.Days);
        Assert.Null(read.Figures);
        Assert.Empty(Assert.Single(await WithStoreAsync(app, s => s.StatementsOfAsync(id, default))).Days);
    }

    [Fact]
    public async Task Validate_freezes_the_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id), default));
        var version = (await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default)))!.Version;
        Assert.True((await WithStoreAsync(app, s => s.ValidateAsync(id, September, Figures, DraftOf(id).Days, version, default))).IsSuccess);
        var read = await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default));
        Assert.True(read!.Validated);
        Assert.Equal(Figures, read.Figures);
        Assert.Equal(Figures, Assert.Single(await WithStoreAsync(app, s => s.StatementsOfAsync(id, default))).Figures);
    }

    [Fact]
    public async Task Validated_statement_refuses_a_draft_save()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id), default));
        var version = (await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default)))!.Version;
        await WithStoreAsync(app, s => s.ValidateAsync(id, September, Figures, DraftOf(id).Days, version, default));
        var stored = (await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default)))!;
        var saved = await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id, stored.Version), default));
        Assert.True(saved.TryGetFailure(out var failure));
        Assert.Equal(ChildcareCodes.StatementValidated, failure!.Code);

        await WithStoreAsync(app, async s => { await s.ReopenAsync(id, September, default); return 0; });
        var reopened = await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default));
        Assert.False(reopened!.Validated);
        Assert.Null(reopened.Figures);
    }

    [Fact]
    public async Task Stale_statement_version_conflicts()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id), default));
        var version = (await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default)))!.Version;
        Assert.True((await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id, version), default))).IsSuccess);
        var stale = await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id, version), default));
        Assert.True(stale.TryGetFailure(out var failure));
        Assert.Equal(ChildcareCodes.StatementConflict, failure!.Code);
        var duplicate = await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id), default));
        Assert.True(duplicate.TryGetFailure(out failure));
        Assert.Equal(ChildcareCodes.StatementConflict, failure!.Code);
    }

    [Fact]
    public async Task Discard_removes_the_draft()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreatedAsync(app);
        await WithStoreAsync(app, s => s.SaveDraftAsync(DraftOf(id), default));
        await WithStoreAsync(app, async s => { await s.DiscardAsync(id, September, default); return 0; });
        Assert.Null(await WithStoreAsync(app, s => s.FindStatementAsync(id, September, default)));
    }
}
