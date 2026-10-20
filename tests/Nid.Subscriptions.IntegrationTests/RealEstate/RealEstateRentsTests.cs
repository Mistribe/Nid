using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Income dashboard spec §5: the rents RealEstate exposes to Income — rent only, prorated, dated on the first covered day. Today is 2025-10-15.</summary>
[Collection("postgres")]
public class RealEstateRentsTests(PostgresFixture fixture)
{
    private static readonly DateRange Autumn = DateRange.Create(new LocalDate(2025, 9, 1), new LocalDate(2025, 12, 1)).Ok();

    private static async Task LeaseAsync(NidApp app, PersonId actor, Guid property, string startsOn, string? endsOn = null)
        => await CreatedAsync(await app.RawAsync(actor, HttpMethod.Post, $"{Properties}/{property}/tenancies",
            new { label = "Bail", startsOn, endsOn, rentMonthly = "800", chargesMonthly = "60" }));

    private static async Task<IReadOnlyList<RentSource>> RentsAsync(NidApp app, PersonId reader, VisibilityScope scope = VisibilityScope.Household, DateRange? range = null)
    {
        await using var rentScope = app.Services.CreateAsyncScope();
        rentScope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var result = await rentScope.ServiceProvider.GetRequiredService<IRentIncome>().RentsAsync(
            reader, scope, range ?? Autumn, [new LocalDate(2025, 10, 15)], NidApp.GoldenCaseToday, default);
        return result.TryGetValue(out var sources) ? sources : throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_tenancy_gives_one_rent_due_per_covered_month_without_charges()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await LeaseAsync(app, app.AdultA, await NewPropertyAsync(app, Home(app)), "2025-09-16");

        var source = Assert.Single(await RentsAsync(app, app.AdultA));

        Assert.Equal([(new LocalDate(2025, 9, 16), 400m), (new LocalDate(2025, 10, 1), 800m), (new LocalDate(2025, 11, 1), 800m)],
            source.Dues.Select(d => (d.On, d.Amount)));
        Assert.Equal(800m, source.MonthlyAt[new LocalDate(2025, 10, 15)]);
        Assert.Equal(new RentDue(new LocalDate(2025, 11, 1), 800m), source.Next);
        Assert.Equal(("shared", false), (source.Visibility, source.Archived));
    }

    [Fact]
    public async Task An_ended_tenancy_without_a_follower_gives_its_end()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var alone = await NewPropertyAsync(app, Home(app, "Seule"));
        var followed = await NewPropertyAsync(app, Home(app, "Suivie"));
        await LeaseAsync(app, app.AdultA, alone, "2025-01-01", "2026-01-01");
        await LeaseAsync(app, app.AdultA, followed, "2025-01-01", "2026-01-01");
        await LeaseAsync(app, app.AdultA, followed, "2026-01-01");

        var sources = (await RentsAsync(app, app.AdultA)).ToDictionary(s => s.Name);

        Assert.Equal(new LocalDate(2026, 1, 1), sources["Seule"].EndsOn);
        Assert.Null(sources["Suivie"].EndsOn);
    }

    [Fact]
    public async Task The_other_adults_private_property_never_appears()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewPropertyAsync("Secret", Visibility.Private, (app.AdultB, 10_000));
        await LeaseAsync(app, app.AdultB, secret.Value, "2025-01-01");

        Assert.Empty(await RentsAsync(app, app.AdultA, VisibilityScope.Me));
        Assert.Empty(await RentsAsync(app, app.AdultA));
        Assert.Empty(await RentsAsync(app, app.AdultB));
        Assert.Equal("private", Assert.Single(await RentsAsync(app, app.AdultB, VisibilityScope.Me)).Visibility);
    }

    [Fact]
    public async Task An_archived_property_keeps_its_past_dues()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await LeaseAsync(app, app.AdultA, id, "2025-01-01", "2025-10-01");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
                .ArchiveAsync(new ItemId(id), Instant.FromUtc(2025, 10, 2, 0, 0), default)).IsSuccess);
        }

        var source = Assert.Single(await RentsAsync(app, app.AdultA));

        Assert.True(source.Archived);
        Assert.Equal([new LocalDate(2025, 9, 1)], source.Dues.Select(d => d.On));
    }

    [Fact]
    public async Task A_mid_month_window_drops_the_dues_dated_outside_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await LeaseAsync(app, app.AdultA, id, "2025-01-01", "2025-11-20");
        await LeaseAsync(app, app.AdultA, id, "2025-11-20");

        var window = DateRange.Create(new LocalDate(2025, 9, 16), new LocalDate(2025, 11, 15)).Ok();
        var source = Assert.Single(await RentsAsync(app, app.AdultA, range: window));

        Assert.Equal([new LocalDate(2025, 10, 1), new LocalDate(2025, 11, 1)], source.Dues.Select(d => d.On)); // 09-01 is before the window, 11-20 after its end
    }

    [Fact]
    public async Task An_open_range_reads_up_to_the_month_of_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await LeaseAsync(app, app.AdultA, await NewPropertyAsync(app, Home(app)), "2025-01-01");

        var open = DateRange.Create(new LocalDate(2025, 9, 1), null).Ok();
        var source = Assert.Single(await RentsAsync(app, app.AdultA, range: open));

        Assert.Equal([new LocalDate(2025, 9, 1), new LocalDate(2025, 10, 1)], source.Dues.Select(d => d.On));
    }
}
