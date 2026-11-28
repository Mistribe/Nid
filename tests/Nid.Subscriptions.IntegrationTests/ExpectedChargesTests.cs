using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>5C spec §6.1: the phase charges Banking expects to see on a statement.</summary>
public class ExpectedChargesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<IReadOnlyList<ExpectedCharge>> ChargesAsync(NidApp app, PersonId reader, LocalDate from, LocalDate to, VisibilityScope seen = VisibilityScope.Me)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var result = await scope.ServiceProvider.GetServices<IExpectedCharges>().Single(c => c.GetType().Name == "SubscriptionExpectedCharges")
            .ChargesAsync(reader, seen, from, to, default);
        return result.TryGetValue(out var charges) ? charges : throw new InvalidOperationException();
    }

    private static async Task<ItemId> MonthlyAsync(NidApp app, PersonId who, string name, Visibility visibility)
    {
        var id = await app.NewSubscriptionAsync(name, who, visibility);
        (await app.RawAsync(who, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2026-01-05",
            price = new { amount = "12.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task A_monthly_phase_gives_one_charge_per_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await MonthlyAsync(app, app.AdultA, "Netflix", Visibility.Shared);

        var charges = await ChargesAsync(app, app.AdultA, new LocalDate(2026, 3, 1), new LocalDate(2026, 4, 30));

        Assert.Equal(["2026-03-05", "2026-04-05"], charges.Select(c => c.Key));
        Assert.All(charges, c => Assert.Equal(("subscriptions", ChargeKinds.Subscription, "subscriptions", 12.99m, false, 5), (c.Module, c.Kind.Name, c.Kind.Page, c.Amount.Amount, c.Credit, c.ToleranceDays)));
        Assert.All(charges, c => Assert.Contains("Netflix", c.LabelHints));
    }

    [Fact]
    public async Task A_private_subscription_is_its_owner_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await MonthlyAsync(app, app.AdultA, "SecretTherapy", Visibility.Private);
        var (from, to) = (new LocalDate(2026, 3, 1), new LocalDate(2026, 4, 30));

        Assert.Empty(await ChargesAsync(app, app.AdultB, from, to));
        Assert.Equal(2, (await ChargesAsync(app, app.AdultA, from, to)).Count);
        // #241: the household scope shows the shared items only, the reader's private ones left out.
        Assert.Empty(await ChargesAsync(app, app.AdultA, from, to, VisibilityScope.Household));
    }

    [Fact]
    public async Task A_prepaid_pack_is_a_recorded_charge_not_an_expected_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Game", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2026-03-10", label = "Pack 1 an", kind = "prepaid_term",
            price = new { amount = "60.00", currency = "EUR" },
            payer = new { type = "household" },
            // Two grants, one purchase: one charge to link.
            grants = new[] { 12, 1 }.Select(months => new { subscriptionId = id.Value, duration = new { count = months, unit = "month" }, source = "paid" }).ToArray(),
        })).EnsureSuccessStatusCode();
        var (from, to) = (new LocalDate(2026, 3, 1), new LocalDate(2026, 3, 31));

        Assert.Empty(await ChargesAsync(app, app.AdultA, from, to));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        var recorded = await Assert.Single(scope.ServiceProvider.GetServices<IRecordedCharges>()).ChargesAsync(app.AdultA, VisibilityScope.Me, from, to, TestContext.Current.CancellationToken);
        var charge = Assert.Single(recorded.TryGetValue(out var found) ? found : throw new InvalidOperationException());
        Assert.Equal((id, ChargeKinds.Purchase, new LocalDate(2026, 3, 10), 60.00m, false), (charge.Item, charge.Kind.Name, charge.DueOn, charge.Amount.Amount, charge.Credit));
        Assert.StartsWith("purchase:", charge.Key, StringComparison.Ordinal);
    }
}
