using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>5C spec §6.1: the phase charges Banking expects to see on a statement.</summary>
public class ExpectedChargesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<IReadOnlyList<ExpectedCharge>> ChargesAsync(NidApp app, PersonId reader, LocalDate from, LocalDate to)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var result = await scope.ServiceProvider.GetServices<IExpectedCharges>().Single(c => c.GetType().Name == "SubscriptionExpectedCharges")
            .ChargesAsync(reader, from, to, default);
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
        Assert.All(charges, c => Assert.Equal(("subscriptions", ChargeKind.Subscription, 12.99m, false, 5), (c.Module, c.Kind, c.Amount.Amount, c.Credit, c.ToleranceDays)));
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
    }
}
