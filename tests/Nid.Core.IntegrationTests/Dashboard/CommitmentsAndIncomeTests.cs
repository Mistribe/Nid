using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Dashboard;

public class CommitmentsAndIncomeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope)
        => JsonDocument.Parse(await app.GetRawAsync(reader, $"/api/v1/dashboard?scope={scope}")).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    [Fact]
    public async Task Commitments_and_income_give_the_margin_and_the_share()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 9.99m);
        await app.NewIncomeSourceAsync("Salaire", Visibility.Shared, (app.AdultA, 10_000)); // 1000 EUR monthly from 2025-01-01

        // « me »: the reader's shared and private items alike, whatever visibility the monthly helper gives.
        var view = await DashboardAsync(app, app.AdultA, "me");

        Assert.Equal(1, view.GetProperty("subscriptions").GetInt32());
        Assert.NotNull(Total(view.GetProperty("commitments")));
        Assert.Equal(1, view.GetProperty("incomeSources").GetInt32());
        Assert.Equal("1000.00", Total(view.GetProperty("income")));
        var commitments = decimal.Parse(Total(view.GetProperty("commitments"))!, CultureInfo.InvariantCulture);
        var margin = decimal.Parse(view.GetProperty("margin").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(margin, 1000m - commitments - 0.01m, 1000m - commitments + 0.01m); // margin from exact values, totals rounded
        Assert.Equal(JsonValueKind.Number, view.GetProperty("committedPct").ValueKind);
    }

    [Fact]
    public async Task A_source_without_a_running_phase_is_declared_income_of_zero_not_unknown()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewIncomeSourceAsync("Pension à venir", app.AdultA); // no phase

        var view = await DashboardAsync(app, app.AdultA, "household");

        Assert.Equal(1, view.GetProperty("incomeSources").GetInt32());
        Assert.Equal("0.00", Total(view.GetProperty("income")));
    }

    private static async Task LeaseAsync(NidApp app, Guid property, string startsOn, string? endsOn = null, PersonId? actor = null)
        => await CreatedAsync(await app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies",
            new { label = "Bail", startsOn, endsOn, rentMonthly = "800", chargesMonthly = "60" }));

    [Fact]
    public async Task A_running_tenancy_adds_its_rent_without_charges_to_income_and_margin()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 9.99m);
        await app.NewIncomeSourceAsync("Salaire", Visibility.Shared, (app.AdultA, 10_000)); // 1000 EUR monthly each
        await app.NewIncomeSourceAsync("Prime", Visibility.Shared, (app.AdultA, 10_000));
        await LeaseAsync(app, await NewPropertyAsync(app, Home(app)), "2025-01-01");

        var view = await DashboardAsync(app, app.AdultA, "me");

        Assert.Equal(3, view.GetProperty("incomeSources").GetInt32());
        Assert.Equal("2800.00", Total(view.GetProperty("income")));
        var commitments = decimal.Parse(Total(view.GetProperty("commitments"))!, CultureInfo.InvariantCulture);
        var margin = decimal.Parse(view.GetProperty("margin").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(margin, 2800m - commitments - 0.01m, 2800m - commitments + 0.01m);
    }

    [Fact]
    public async Task The_home_income_is_the_income_pages_monthly_figure()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewIncomeSourceAsync("Salaire", Visibility.Shared, (app.AdultA, 10_000));
        await LeaseAsync(app, await NewPropertyAsync(app, Home(app)), "2025-01-01");

        foreach (var scope in new[] { "household", "me" })
        {
            var overview = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/income/overview?scope={scope}")).RootElement;
            var monthlyNow = overview.GetProperty("figures").GetProperty("monthlyNow").GetProperty("amount").GetString();
            Assert.Equal("1800.00", monthlyNow);
            Assert.Equal(monthlyNow, Total((await DashboardAsync(app, app.AdultA, scope)).GetProperty("income")));
            // The home's flow draws today's split of that same figure, largest first, each share with its module.
            var now = overview.GetProperty("nowSplit").EnumerateArray()
                .Select(s => (s.GetProperty("module").GetString(), s.GetProperty("perMonth").GetProperty("amount").GetString(), s.GetProperty("percent").GetDecimal()))
                .ToList();
            Assert.Equal([("income", "1000.00", 55.6m), ("realestate", "800.00", 44.4m)], now);
        }
    }

    [Fact]
    public async Task A_rental_alone_is_declared_income()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await LeaseAsync(app, await NewPropertyAsync(app, Home(app)), "2025-01-01");

        var view = await DashboardAsync(app, app.AdultA, "household");

        Assert.Equal(1, view.GetProperty("incomeSources").GetInt32());
        Assert.Equal("800.00", Total(view.GetProperty("income")));
    }

    [Fact]
    public async Task A_property_whose_tenancies_all_ended_adds_no_source()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await LeaseAsync(app, await NewPropertyAsync(app, Home(app)), "2024-01-01", "2025-06-01");

        var view = await DashboardAsync(app, app.AdultA, "household");

        Assert.Equal(0, view.GetProperty("incomeSources").GetInt32());
        Assert.Null(Total(view.GetProperty("income")));
    }

    [Fact]
    public async Task A_lease_starting_later_makes_income_declared_with_no_rent_yet()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await LeaseAsync(app, await NewPropertyAsync(app, Home(app)), "2026-01-01");

        var view = await DashboardAsync(app, app.AdultA, "household");

        Assert.Equal(1, view.GetProperty("incomeSources").GetInt32());
        Assert.Equal("0.00", Total(view.GetProperty("income")));
    }

    [Fact]
    public async Task An_archived_rented_property_is_not_counted()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewPropertyAsync(app, Home(app));
        await LeaseAsync(app, id, "2025-01-01");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            Assert.True((await scope.ServiceProvider.GetRequiredService<IItemRegistry>()
                .ArchiveAsync(new ItemId(id), Instant.FromUtc(2025, 10, 2, 0, 0), TestContext.Current.CancellationToken)).IsSuccess);
        }

        var view = await DashboardAsync(app, app.AdultA, "household");

        Assert.Equal(0, view.GetProperty("incomeSources").GetInt32());
        Assert.Null(Total(view.GetProperty("income")));
    }

    [Theory]
    [InlineData("household")]
    [InlineData("me")]
    public async Task The_other_adults_private_items_enter_no_total_and_no_count(string scope)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Secret", app.AdultB, Visibility.Private);
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultB, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf = "2025-10-01", amount = new { amount = "5000.00", currency = "EUR" }, note = (string?)null })).StatusCode);
        await app.NewSubscriptionAsync("Secret", app.AdultB, Visibility.Private);
        await app.NewIncomeSourceAsync("Secret", Visibility.Private, (app.AdultB, 10_000));
        var secret = await app.NewPropertyAsync("Secret", Visibility.Private, (app.AdultB, 10_000));
        await LeaseAsync(app, secret.Value, "2025-01-01", actor: app.AdultB);

        var view = await DashboardAsync(app, app.AdultA, scope);

        Assert.Equal(0, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(0, view.GetProperty("subscriptions").GetInt32());
        Assert.Equal(0, view.GetProperty("incomeSources").GetInt32());
        Assert.Null(Total(view.GetProperty("netWorth")));
        Assert.Null(Total(view.GetProperty("income")));
    }
}
