using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

[Collection("postgres")]
public class NetWorthContributionTests(PostgresFixture fixture)
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task ValueAsync(NidApp app, PersonId owner, ItemId envelope, string asOf, string amount)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(owner, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations", new { asOf, amount = Eur(amount), note = (string?)null })).StatusCode);

    private static async Task ContributionAsync(NidApp app, PersonId owner, ItemId envelope, string on, string amount)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(owner, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/flows", new { occurredOn = on, kind = "contribution", amount = Eur(amount) })).StatusCode);

    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await (await app.RawAsync(reader, HttpMethod.Get, $"/api/v1/dashboard?scope={scope}")).Content.ReadAsStringAsync()).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    [Fact]
    public async Task Net_worth_is_the_latest_values_and_the_change_excludes_contributions()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, pea, "2024-12-31", "10000.00");
        await ContributionAsync(app, app.AdultA, pea, "2025-06-15", "1000.00");
        await ValueAsync(app, app.AdultA, pea, "2025-10-01", "12000.00");

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal("12000.00", Total(view.GetProperty("netWorth")));
        Assert.Equal("2025-10-01", view.GetProperty("valuesAsOf").GetString());
        Assert.Equal(1, view.GetProperty("envelopes").GetInt32());
        Assert.Equal("1000.00", Total(view.GetProperty("change")));
        Assert.Equal("1000.00", Total(view.GetProperty("changeContributions")));
        Assert.Equal(9.1m, view.GetProperty("changePct").GetDecimal());
    }

    [Fact]
    public async Task An_envelope_without_a_value_is_counted_apart_never_as_zero_and_a_stale_one_is_flagged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewEnvelopeAsync("Vide", app.AdultA, Visibility.Shared);
        var old = await app.NewEnvelopeAsync("Ancien", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, old, "2025-08-01", "500.00"); // monthly cadence: stale after 45 days

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal(2, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(1, view.GetProperty("envelopesWithoutValue").GetInt32());
        Assert.Equal(1, view.GetProperty("staleEnvelopes").GetInt32());
        Assert.Equal("500.00", Total(view.GetProperty("netWorth")));
        Assert.Null(Total(view.GetProperty("change"))); // nothing on or before 1 January
    }

    [Fact]
    public async Task A_dependents_shared_envelope_counts_in_the_household_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var child = await app.NewEnvelopeAsync("Livret enfant", app.Dependent, Visibility.Shared);
        await ValueAsync(app, app.AdultA, child, "2025-10-01", "300.00");

        Assert.Equal("300.00", Total((await DashboardAsync(app, app.AdultA)).GetProperty("netWorth")));
    }

    // Forwards to the real IValuations, but its series of a chosen item fails (so that item's facts cannot be read).
    private class FailingSeries : DispatchProxy
    {
        public static IValuations? Inner;
        public static ItemId? Broken;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IValuations.SeriesAsync) && (ItemId)args![0]! == Broken)
                return Task.FromResult(Result.Failure<IReadOnlyList<Valuation>>(new Failure("test.broken", "Series unreadable.", null)));
            return method.Invoke(Inner, args);
        }
    }

    [Fact]
    public async Task An_envelope_whose_facts_cannot_be_read_counts_as_without_value_never_silently_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddScoped<IValuations>(sp =>
            {
                var proxy = DispatchProxy.Create<IValuations, FailingSeries>();
                FailingSeries.Inner = ActivatorUtilities.CreateInstance(sp, typeof(Nid.Core.Infrastructure.Persistence.Valuations)) as IValuations;
                return proxy;
            });
        });
        var good = await app.NewEnvelopeAsync("Bon", app.AdultA, Visibility.Shared);
        var broken = await app.NewEnvelopeAsync("Illisible", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, good, "2025-10-01", "500.00");
        FailingSeries.Broken = broken;

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal(2, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(1, view.GetProperty("envelopesWithoutValue").GetInt32());
        Assert.Equal("500.00", Total(view.GetProperty("netWorth")));
    }
}
