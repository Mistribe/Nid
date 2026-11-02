using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Passive income spec C7, §3.3: the net rent RealEstate exposes to Investments. Today is 2025-10-15.</summary>
public class PropertyIncomeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateRange Autumn = DateRange.Create(new LocalDate(2025, 9, 1), new LocalDate(2025, 12, 1)).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    private static async Task LeaseAsync(NidApp app, PersonId actor, Guid property, string startsOn, string? endsOn = null)
        => await CreatedAsync(await app.RawAsync(actor, HttpMethod.Post, $"{Properties}/{property}/tenancies",
            new { label = "Bail", startsOn, endsOn, rentMonthly = "800", chargesMonthly = "60" }));

    private static async Task FlowAsync(NidApp app, Guid property, string kind, string category, string on, string amount)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/flows", new { occurredOn = on, kind, amount, category }));

    private static async Task<IReadOnlyList<PropertyIncomeSource>> IncomeAsync(NidApp app, PersonId reader, VisibilityScope scope = VisibilityScope.Household)
    {
        await using var incomeScope = app.Services.CreateAsyncScope();
        incomeScope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var result = await incomeScope.ServiceProvider.GetRequiredService<IPropertyIncome>().IncomeAsync(
            reader, scope, Autumn, [new LocalDate(2025, 9, 30), new LocalDate(2025, 1, 1)], NidApp.GoldenCaseToday, default);
        return result.TryGetValue(out var sources) ? sources : throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_let_property_gives_one_month_per_month_of_the_range_and_its_latest_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await LeaseAsync(app, app.AdultA, id, "2025-01-01");
        foreach (var (asOf, amount) in new[] { ("2025-03-01", "280000"), ("2025-06-01", "290000"), ("2025-10-01", "999999") })
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf, amount }));
        await FlowAsync(app, id, "income", "rent", "2025-10-05", "800");
        await FlowAsync(app, id, "expense", "property_tax", "2025-10-10", "1200");
        await FlowAsync(app, id, "expense", "works", "2025-10-10", "5000");

        var source = Assert.Single(await IncomeAsync(app, app.AdultA));

        Assert.Equal([new LocalDate(2025, 9, 1), new LocalDate(2025, 10, 1), new LocalDate(2025, 11, 1)], source.Months.Select(m => m.Month));
        Assert.Equal((800m, 1200m), (source.Months[1].Rent, source.Months[1].Costs));
        Assert.Equal(290000m, source.ValueAt[new LocalDate(2025, 9, 30)]);
        Assert.Null(source.ValueAt[new LocalDate(2025, 1, 1)]);
        Assert.True(source.LetToday);
        Assert.False(source.Archived);
    }

    [Fact]
    public async Task A_property_without_a_tenancy_gives_no_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await FlowAsync(app, await NewPropertyAsync(app, Home(app)), "expense", "property_tax", "2025-10-10", "1200");

        Assert.Empty(await IncomeAsync(app, app.AdultA));
    }

    [Fact]
    public async Task The_other_adults_private_property_never_appears()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewPropertyAsync("Secret", Visibility.Private, (app.AdultB, 10_000));
        await LeaseAsync(app, app.AdultB, secret.Value, "2025-01-01");

        Assert.Empty(await IncomeAsync(app, app.AdultA, VisibilityScope.Me));
        Assert.Empty(await IncomeAsync(app, app.AdultA));
    }

    [Fact]
    public async Task An_archived_property_let_in_the_range_stays_but_is_not_let_today()
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

        var source = Assert.Single(await IncomeAsync(app, app.AdultA));

        Assert.True(source.Archived);
        Assert.False(source.LetToday);
    }
}
