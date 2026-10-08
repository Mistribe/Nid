using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

[Collection("postgres")]
public class CommitmentsAndIncomeTests(PostgresFixture fixture)
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

        var view = await DashboardAsync(app, app.AdultA, scope);

        Assert.Equal(0, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(0, view.GetProperty("subscriptions").GetInt32());
        Assert.Equal(0, view.GetProperty("incomeSources").GetInt32());
        Assert.Null(Total(view.GetProperty("netWorth")));
        Assert.Null(Total(view.GetProperty("income")));
    }
}
