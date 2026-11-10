using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using NodaTime.Text;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>
/// Issue #12: the annual review reads past months' commitments with <c>includeArchived</c>; an archived item counts only the cash
/// due strictly before its archive day. Without the flag (the dashboard), archived items stay out.
/// </summary>
public class ArchivedCommitmentsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Archives an item on a day, mid-morning UTC: the same day in the household's zone.</summary>
    private static async Task ArchiveAsync(NidApp app, Guid id, int year, int month, int day)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
            .ArchiveAsync(new ItemId(id), Instant.FromUtc(year, month, day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);
    }

    /// <summary>The part of <paramref name="source"/> in the month of <paramref name="on"/>, read by A over the household.</summary>
    private static async Task<CommitmentsPart> PartAsync(NidApp app, string source, string on, bool includeArchived)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var parts = new List<CommitmentsPart>();
        foreach (var contribution in scope.ServiceProvider.GetServices<ICommitmentsContribution>())
        {
            var result = await contribution.CommitmentsAsync(app.AdultA, VisibilityScope.Household, LocalDatePattern.Iso.Parse(on).Value,
                TestContext.Current.CancellationToken, includeArchived);
            Assert.True(result.TryGetValue(out var part));
            parts.Add(part!);
        }
        return parts.Single(p => p.Source == source);
    }

    private static async Task<decimal> CashAsync(NidApp app, string source, string on, bool includeArchived)
        => (await PartAsync(app, source, on, includeArchived)).Cash.GetValueOrDefault(Currency.Eur);

    private const string Subscriptions = CommitmentSources.Subscriptions, Loans = CommitmentSources.Loans, PropertyCosts = CommitmentSources.PropertyCosts;

    [Fact]
    public async Task The_tax_commits_nothing_in_a_year_before_its_rules()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var part = await PartAsync(app, CommitmentSources.Tax, "2024-03-31", includeArchived: true);

        Assert.Equal((0, 0, 0), (part.Count, part.Amortized.Count, part.Cash.Count));
    }

    [Theory]
    [InlineData("2024-06-15")]
    [InlineData(null)] // archived while still running: the archive day ends it
    public async Task An_archived_subscription_counts_its_past_charges(string? endsOn)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Presse", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2024-01-01", endsOn,
            price = new { amount = "30", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        await ArchiveAsync(app, id.Value, 2024, 6, 15);

        Assert.Equal(30m, await CashAsync(app, Subscriptions, "2024-03-31", includeArchived: true));
        Assert.Equal(0m, await CashAsync(app, Subscriptions, "2024-07-31", includeArchived: true));
        Assert.Equal(0m, await CashAsync(app, Subscriptions, "2024-03-31", includeArchived: false));
    }

    [Fact]
    public async Task An_archived_loan_counts_payments_before_its_archive_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await NewAsync(app, Short(app, "Prêt travaux")); // 1 000 on each 15th from 2025-01-15
        await ArchiveAsync(app, loan, 2025, 6, 15);

        Assert.Equal(1000m, await CashAsync(app, Loans, "2025-05-31", includeArchived: true));
        Assert.Equal(0m, await CashAsync(app, Loans, "2025-06-30", includeArchived: true)); // due on its archive day
        Assert.Equal(0m, await CashAsync(app, Loans, "2025-07-31", includeArchived: true));
        Assert.Equal(0m, await CashAsync(app, Loans, "2025-05-31", includeArchived: false));
    }

    [Fact]
    public async Task An_archived_property_counts_costs_due_before_its_archive_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app));
        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/recurring-costs",
            new { category = "condo_fees", amount = "150", every = new { count = 1, unit = "month" }, startsOn = "2025-01-05" })).IsSuccessStatusCode);
        Assert.True((await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/recurring-costs",
            new { category = "insurance", amount = "50", every = new { count = 1, unit = "month" }, startsOn = "2025-06-10" })).IsSuccessStatusCode);
        await ArchiveAsync(app, home, 2025, 6, 5);

        Assert.Equal(150m, await CashAsync(app, PropertyCosts, "2025-05-31", includeArchived: true));
        var june = await PartAsync(app, PropertyCosts, "2025-06-30", includeArchived: true);
        Assert.Equal(0m, june.Cash.GetValueOrDefault(Currency.Eur)); // due on its archive day
        // Amortized: what ran on its last counted day (2025-06-04), not the cost starting after its sale.
        Assert.Equal((1, 150m), (june.Count, june.Amortized.GetValueOrDefault(Currency.Eur)));
        Assert.Equal(0m, await CashAsync(app, PropertyCosts, "2025-05-31", includeArchived: false));
    }

    [Fact]
    public async Task The_dashboard_ignores_archived_items()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var sub = await app.NewMonthlyAsync(app.AdultA, 9.99m); // charged on 2025-10-01, before its archive day
        var loan = await NewAsync(app, Short(app, "Prêt travaux")); // still paying in October
        await ArchiveAsync(app, sub.Value, 2025, 10, 10);
        await ArchiveAsync(app, loan, 2025, 10, 10);

        var view = System.Text.Json.JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement;

        Assert.Equal(0, view.GetProperty("subscriptions").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("cashThisMonth").GetProperty("total").ValueKind);
    }
}
