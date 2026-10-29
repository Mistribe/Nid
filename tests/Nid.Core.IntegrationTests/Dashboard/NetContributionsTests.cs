using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>Projection spec §2.1: what the household put into its envelopes over a period, per currency, as the dashboard's contributions read it.</summary>
public class NetContributionsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task FlowAsync(NidApp app, PersonId writer, ItemId envelope, string kind, string on, string amount, string currency = "EUR")
        => (await app.RawAsync(writer, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/flows",
            new { occurredOn = on, kind, amount = new { amount, currency } })).EnsureSuccessStatusCode();

    private static async Task<Dictionary<Currency, decimal>> NetContributionsAsync(NidApp app, PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to)
    {
        await using var requestScope = app.Services.CreateAsyncScope();
        requestScope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var sums = new Dictionary<Currency, decimal>();
        foreach (var contribution in requestScope.ServiceProvider.GetServices<INetWorthContribution>())
        {
            var result = await contribution.NetContributionsAsync(reader, scope, from, to, default);
            foreach (var (currency, amount) in result.TryGetValue(out var part) ? part : throw new InvalidOperationException())
                sums[currency] = sums.GetValueOrDefault(currency) + amount;
        }
        return sums;
    }

    [Fact]
    public async Task Net_contributions_sum_visible_envelopes_per_currency()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await FlowAsync(app, app.AdultA, pea, "opening", "2025-02-01", "10000");
        await FlowAsync(app, app.AdultA, pea, "contribution", "2025-03-01", "300");
        await FlowAsync(app, app.AdultA, pea, "withdrawal", "2025-04-01", "100");
        await FlowAsync(app, app.AdultA, pea, "contribution", "2024-12-31", "50"); // before the window
        await FlowAsync(app, app.AdultA, pea, "expense", "2025-05-01", "20");
        var closed = await app.NewEnvelopeAsync("Old livret", app.AdultA, Visibility.Shared);
        await FlowAsync(app, app.AdultA, closed, "contribution", "2025-02-15", "500");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{closed.Value}/close", new { closedOn = "2025-05-31" }))
            .EnsureSuccessStatusCode();
        var dollars = await app.NewEnvelopeAsync("Brokerage", app.AdultA, Visibility.Shared, "USD");
        await FlowAsync(app, app.AdultA, dollars, "contribution", "2025-06-30", "70", "USD");
        var secret = await app.NewEnvelopeAsync("Secret", app.AdultB, Visibility.Private);
        await FlowAsync(app, app.AdultB, secret, "contribution", "2025-03-01", "9999");

        var household = await NetContributionsAsync(app, app.AdultA, VisibilityScope.Household, new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 30));
        var mine = await NetContributionsAsync(app, app.AdultB, VisibilityScope.Me, new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 30));

        Assert.Equal(new Dictionary<Currency, decimal> { [Currency.Eur] = 700m, [Currency.Usd] = 70m }, household);
        Assert.Equal(10_699m, mine[Currency.Eur]); // « Moi » holds the shared envelopes and B's own private one
    }
}
