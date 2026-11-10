using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Issue #12: the yearly review's properties (the app clock is fixed at 2025-10-15, so 2024 is a full year).</summary>
public class PropertiesReviewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Start = new(2023, 12, 31), End = new(2024, 12, 31);

    private static async Task<T> InScopeAsync<T>(NidApp app, Func<IServiceProvider, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        return await read(scope.ServiceProvider);
    }

    private static Task<PropertiesReviewPart> ReviewAsync(NidApp app, LocalDate? start = null, LocalDate? end = null) => InScopeAsync(app, async sp =>
    {
        var review = await sp.GetRequiredService<IPropertiesReview>()
            .ReviewAsync(app.AdultA, VisibilityScope.Household, start ?? Start, end ?? End, TestContext.Current.CancellationToken);
        Assert.True(review.TryGetValue(out var part));
        return part!;
    });

    private static Task<bool> ArchiveAsync(NidApp app, Guid id, LocalDate on) => InScopeAsync(app, async sp =>
        (await sp.GetRequiredService<IItemRegistry>().ArchiveAsync(new ItemId(id), Instant.FromUtc(on.Year, on.Month, on.Day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);

    private static async Task FlowAsync(NidApp app, Guid property, string on, string kind, string amount, string category)
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/flows",
            new { occurredOn = on, kind, amount, category }));

    /// <summary>README §12's purchase (a cost price of 269 000), let since 2021.</summary>
    private static Dictionary<string, object?> Let(NidApp app, string name = "Maison")
    {
        var body = Home(app, name);
        body["type"] = "rental";
        body["rentalStartsOn"] = "2021-01-01";
        return body;
    }

    /// <summary>A rent of 1 000 on the 5th of each of the first <paramref name="months"/> months of <paramref name="year"/>.</summary>
    private static async Task RentsAsync(NidApp app, Guid property, int year, int months)
    {
        for (var month = 1; month <= months; month++)
            await FlowAsync(app, property, $"{year}-{month:00}-05", "income", "1000", "rent");
    }

    [Fact]
    public async Task Costs_by_category_match_the_costs_page()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Let(app));
        await FlowAsync(app, home, "2023-09-20", "expense", "1400", "property_tax"); // before the year
        await FlowAsync(app, home, "2024-03-01", "expense", "5000", "works");
        await FlowAsync(app, home, "2024-09-20", "expense", "1500", "property_tax");
        await FlowAsync(app, home, "2024-04-10", "expense", "300", "condo_fees");
        await FlowAsync(app, home, "2024-10-10", "expense", "400", "condo_fees");
        await FlowAsync(app, home, "2025-01-10", "expense", "350", "condo_fees"); // after the year
        await RentsAsync(app, home, 2024, 12);

        var part = await ReviewAsync(app);
        var costs = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Properties}/overview/costs?scope=household"));
        var year = costs.GetProperty("byYear").GetProperty("years").EnumerateArray().Select(y => y.GetProperty("year").GetInt32()).ToList().IndexOf(2024);
        var page = costs.GetProperty("byYear").GetProperty("categories").EnumerateArray()
            .Select(c => (Category: S(c, "category")!, Paid: c.GetProperty("cells")[year].GetProperty("paid")))
            .Where(c => c.Paid.ValueKind != System.Text.Json.JsonValueKind.Null)
            .Select(c => (c.Category, decimal.Parse(S(c.Paid, "amount")!, CultureInfo.InvariantCulture)))
            .OrderBy(c => c.Category, StringComparer.Ordinal).ToList();

        var row = Assert.Single(part.Properties);
        Assert.Equal(page, row.ByCategory.Select(c => (c.Category, c.Amount!.Value)).OrderBy(c => c.Category, StringComparer.Ordinal).ToList());
        Assert.Equal(["works", "property_tax", "condo_fees"], row.ByCategory.Select(c => c.Category)); // largest first
        Assert.Equal((7_200m, 12_000m), (row.Costs!.Value, row.Rents!.Value));
        Assert.Equal((row.Costs, row.Rents), (part.Costs, part.Rents));
        // 12 000 over 269 000 + 5 000 of works; net of the 2 200 of charges but works.
        Assert.Equal((4.4m, 3.6m), (row.GrossYieldPct!.Value, row.NetYieldPct!.Value));
        Assert.Empty(part.MissingCurrencies);
    }

    [Fact]
    public async Task An_archived_property_counts_before_its_archive_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var sold = await NewPropertyAsync(app, Home(app, "Studio vendu"));
        await FlowAsync(app, sold, "2024-03-10", "expense", "800", "property_tax");
        await FlowAsync(app, sold, "2024-07-01", "expense", "200", "maintenance"); // on its archive day
        var before = await NewPropertyAsync(app, Home(app, "Appartement vendu"));
        await FlowAsync(app, before, "2023-05-10", "expense", "700", "property_tax");
        var later = await NewPropertyAsync(app, Home(app, "Maison vendue"));
        await FlowAsync(app, later, "2024-11-10", "expense", "900", "property_tax");
        Assert.True(await ArchiveAsync(app, sold, new LocalDate(2024, 7, 1)));
        Assert.True(await ArchiveAsync(app, before, new LocalDate(2023, 6, 1)));
        Assert.True(await ArchiveAsync(app, later, new LocalDate(2025, 3, 1)));

        var part = await ReviewAsync(app);

        Assert.Equal(["Maison vendue", "Studio vendu"], part.Properties.Select(p => p.Name));
        var row = part.Properties.Single(p => p.Name == "Studio vendu");
        Assert.Equal(800m, row.Costs);
        Assert.Equal(("property_tax", 800m), Assert.Single(row.ByCategory) is var c ? (c.Category, c.Amount!.Value) : default);
        Assert.Equal(1_700m, part.Costs);
    }

    [Fact]
    public async Task An_archived_propertys_rents_and_yield_stop_at_its_last_counted_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var sold = await NewPropertyAsync(app, Let(app, "Studio vendu"));
        await RentsAsync(app, sold, 2024, 7); // the July rent falls after the archive day
        await FlowAsync(app, sold, "2024-07-01", "income", "1000", "rent"); // on the archive day
        Assert.True(await ArchiveAsync(app, sold, new LocalDate(2024, 7, 1)));

        var row = Assert.Single((await ReviewAsync(app)).Properties);

        Assert.Equal(6_000m, row.Rents);
        Assert.Equal((2.2m, 2.2m), (row.GrossYieldPct!.Value, row.NetYieldPct!.Value)); // 6 000 over 269 000, not the 8 000 recorded in the year
    }

    [Fact]
    public async Task The_year_to_date_runs_to_today_without_annualising()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Let(app));
        await RentsAsync(app, home, 2024, 12); // the year before
        await RentsAsync(app, home, 2025, 10);
        await FlowAsync(app, home, "2025-02-20", "expense", "500", "insurance");
        await FlowAsync(app, home, "2024-09-20", "expense", "1500", "property_tax"); // the year before

        var part = await ReviewAsync(app, new LocalDate(2024, 12, 31), new LocalDate(2025, 10, 15));

        var row = Assert.Single(part.Properties);
        Assert.Equal((500m, 10_000m), (row.Costs!.Value, row.Rents!.Value));
        Assert.Equal(("insurance", 500m), Assert.Single(row.ByCategory) is var c ? (c.Category, c.Amount!.Value) : default);
        // 10 000 over 269 000 in nine and a half months, where a year's pace would give 4.7 %; net of the insurance.
        Assert.Equal((3.7m, 3.5m), (row.GrossYieldPct!.Value, row.NetYieldPct!.Value));
    }

    [Fact]
    public async Task A_missing_rate_leaves_the_property_and_the_totals_unknown()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Let(app, "Appartement en dollars");
        body["currency"] = "USD";
        var abroad = await NewPropertyAsync(app, body);
        await FlowAsync(app, abroad, "2024-09-20", "expense", "1500", "property_tax");
        await RentsAsync(app, abroad, 2024, 12);
        var home = await NewPropertyAsync(app, Home(app));
        await FlowAsync(app, home, "2024-09-20", "expense", "800", "property_tax");

        var part = await ReviewAsync(app);

        var row = part.Properties.Single(p => p.Id == new ItemId(abroad));
        Assert.Equal((null, null), (row.Costs, row.Rents));
        Assert.Null(Assert.Single(row.ByCategory).Amount);
        Assert.Equal(4.5m, row.GrossYieldPct); // a ratio in the property's own currency: 12 000 over 269 000
        Assert.Equal(800m, part.Properties.Single(p => p.Id == new ItemId(home)).Costs);
        Assert.Equal((null, null), (part.Costs, part.Rents));
        Assert.Equal(["USD"], part.MissingCurrencies);
    }
}
