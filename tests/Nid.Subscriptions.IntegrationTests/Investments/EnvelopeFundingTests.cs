using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>Savings goals spec §4: what Goals reads of an envelope, only through <see cref="IEnvelopeFunding"/>.</summary>
public class EnvelopeFundingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Today = NidApp.GoldenCaseToday;

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<IReadOnlyList<EnvelopeFundingFigures>> ReadAsync(
        NidApp app, PersonId reader, VisibilityScope scope, params ItemId[] ids)
    {
        await using var services = app.Services.CreateAsyncScope();
        services.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(reader);
        var read = await services.ServiceProvider.GetRequiredService<IEnvelopeFunding>().ReadAsync(reader, scope, ids, Today, default);
        Assert.True(read.TryGetValue(out var figures));
        return figures!;
    }

    private static async Task FlowAsync(NidApp app, ItemId id, LocalDate on, string kind, string amount)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/flows",
            new { occurredOn = on.ToString("yyyy-MM-dd", null), kind, amount = Eur(amount) })).EnsureSuccessStatusCode();

    private static async Task ValuationAsync(NidApp app, ItemId id, LocalDate on, string amount)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/valuations",
            new { asOf = on.ToString("yyyy-MM-dd", null), amount = Eur(amount) })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Reads_value_and_net_contributions_of_last_twelve_months()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        await FlowAsync(app, id, Today.PlusDays(-400), "opening", "1000.00");
        await FlowAsync(app, id, Today.PlusDays(-30), "contribution", "600.00");
        await FlowAsync(app, id, Today.PlusDays(-10), "withdrawal", "100.00");
        await ValuationAsync(app, id, Today.PlusDays(-5), "2000.00");

        var figures = Assert.Single(await ReadAsync(app, app.AdultA, VisibilityScope.Me, id));

        Assert.Equal(id, figures.Envelope);
        Assert.Equal("Livret", figures.Name);
        Assert.False(figures.Closed);
        Assert.Equal(2000m, figures.Value!.Value.Amount);
        Assert.Equal(Today.PlusDays(-5), figures.AsOf);
        Assert.False(figures.Estimated);
        Assert.Equal(500m, figures.NetContributions12Months.Amount);
        Assert.Equal(Currency.Eur, figures.NetContributions12Months.Currency);
    }

    [Fact]
    public async Task Leaves_out_envelopes_the_reader_cannot_see()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var theirs = await app.NewEnvelopeAsync("Privé de B", app.AdultB, Visibility.Private);

        Assert.Empty(await ReadAsync(app, app.AdultA, VisibilityScope.Me, theirs));
    }

    [Fact]
    public async Task Household_scope_leaves_out_own_private_envelope()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var mine = await app.NewEnvelopeAsync("Privé de A", app.AdultA, Visibility.Private);

        Assert.Empty(await ReadAsync(app, app.AdultA, VisibilityScope.Household, mine));
        Assert.Single(await ReadAsync(app, app.AdultA, VisibilityScope.Me, mine));
    }

    [Fact]
    public async Task Closed_envelope_is_marked_closed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Ancien livret", app.AdultA, Visibility.Shared);
        await ValuationAsync(app, id, Today.PlusDays(-60), "100.00");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{id.Value}/close",
            new { closedOn = Today.PlusDays(-30).ToString("yyyy-MM-dd", null) })).EnsureSuccessStatusCode();

        var figures = Assert.Single(await ReadAsync(app, app.AdultA, VisibilityScope.Me, id));

        Assert.True(figures.Closed);
    }

    [Fact]
    public async Task Uses_the_estimate_when_newer()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeEstimateEndpointTests.EnvelopeAsync(app);
        await EnvelopeEstimateEndpointTests.PriceAsync(app, EnvelopeEstimateEndpointTests.EtfIsin, 110m, EnvelopeEstimateEndpointTests.Day);

        var figures = Assert.Single(await ReadAsync(app, app.AdultA, VisibilityScope.Me, id));

        Assert.True(figures.Estimated);
        Assert.Equal(1600m, figures.Value!.Value.Amount);
        Assert.Equal(EnvelopeEstimateEndpointTests.Day, figures.AsOf);
    }

    [Fact]
    public async Task Unknown_or_non_envelope_ids_are_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var subscription = await app.NewSubscriptionAsync("Netflix", app.AdultA, Visibility.Shared);

        Assert.Empty(await ReadAsync(app, app.AdultA, VisibilityScope.Me, subscription, new ItemId(Guid.NewGuid())));
    }

    [Fact]
    public async Task Window_excludes_twelve_months_ago_and_future_flows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        await FlowAsync(app, id, Today.PlusMonths(-12), "contribution", "100.00");
        await FlowAsync(app, id, Today.PlusMonths(-12).PlusDays(1), "contribution", "200.00");
        await FlowAsync(app, id, Today, "contribution", "300.00");

        var figures = Assert.Single(await ReadAsync(app, app.AdultA, VisibilityScope.Me, id));

        Assert.Equal(500m, figures.NetContributions12Months.Amount);
    }
}
