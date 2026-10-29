using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>Batched reads spec §2–§3: the net worth reads its facts as one block per module, and only the reader's items.</summary>
public class BatchedNetWorthTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> DashboardAsync(NidApp app, PersonId reader, string scope = "household")
        => JsonDocument.Parse(await (await app.RawAsync(reader, HttpMethod.Get, $"/api/v1/dashboard?scope={scope}")).Content.ReadAsStringAsync()).RootElement;

    private static string? Total(JsonElement figure)
        => figure.GetProperty("total").ValueKind == JsonValueKind.Null ? null : figure.GetProperty("total").GetProperty("amount").GetString();

    /// <summary>Forwards to the real IValuations, but every batched series fails (a block whose facts cannot be read).</summary>
    public class FailingBlock : DispatchProxy
    {
        public static IValuations? Inner;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method!.Name == nameof(IValuations.SeriesManyAsync)
                ? Task.FromResult(Result.Failure<IReadOnlyDictionary<ItemId, IReadOnlyList<Valuation>>>(new Failure("test.broken", "Series unreadable.", null)))
                : method.Invoke(Inner, args);
    }

    private Task<NidApp> StartWithFailingBlockAsync()
        => NidApp.StartAsync(fixture, services => services.AddScoped<IValuations>(sp =>
        {
            var proxy = DispatchProxy.Create<IValuations, FailingBlock>();
            FailingBlock.Inner = ActivatorUtilities.CreateInstance<Nid.Core.Infrastructure.Persistence.Valuations>(sp);
            return proxy;
        }));

    [Fact]
    public async Task A_block_of_facts_that_cannot_be_read_counts_every_envelope_as_without_value()
    {
        await using var app = await StartWithFailingBlockAsync();
        foreach (var (name, amount) in new[] { ("Premier", "500.00"), ("Second", "700.00") })
        {
            var envelope = await app.NewEnvelopeAsync(name, app.AdultA, Visibility.Shared);
            Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post,
                $"/api/v1/investments/envelopes/{envelope.Value}/valuations", new { asOf = "2025-10-01", amount = Eur(amount), note = (string?)null })).StatusCode);
        }

        var view = await DashboardAsync(app, app.AdultA);

        // Never silently left out: both counted, both without value.
        Assert.Equal(2, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(2, view.GetProperty("envelopesWithoutValue").GetInt32());
    }

    [Fact]
    public async Task A_block_of_estimates_that_cannot_be_read_counts_every_property_as_without_value()
    {
        await using var app = await StartWithFailingBlockAsync();
        foreach (var name in new[] { "Maison", "Appartement" })
        {
            var property = await NewPropertyAsync(app, Home(app, name));
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/valuations", new { asOf = "2025-06-01", amount = "290000" }));
        }

        var view = await DashboardAsync(app, app.AdultA);

        Assert.Equal(2, view.GetProperty("envelopes").GetInt32());
        Assert.Equal(2, view.GetProperty("envelopesWithoutValue").GetInt32());
    }

    [Fact]
    public async Task The_other_adults_private_property_and_loan_leave_each_readers_net_worth_whole()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await NewPropertyAsync(app, Home(app)); // shared, owned by A
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-06-01", amount = "290000" }));
        var secret = await NewPropertyAsync(app, Home(app, "Secret", "private", app.AdultB), app.AdultB);
        await CreatedAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Properties}/{secret}/valuations", new { asOf = "2025-06-01", amount = "100000" }));
        var loan = Short(app, "Prêt de B"); // 2 000 owed today
        loan["propertyId"] = home;
        loan["visibility"] = "private";
        loan["borrowers"] = new[] { new { personId = app.AdultB.Value, shareBp = 10_000 } };
        await NewAsync(app, loan, app.AdultB);

        // A's block holds only A's readable items: a hidden id would make every estimate unreadable.
        foreach (var scope in new[] { "household", "me" })
        {
            var ofA = await DashboardAsync(app, app.AdultA, scope);
            Assert.Equal(("290000.00", 0), (Total(ofA.GetProperty("netWorth")), ofA.GetProperty("envelopesWithoutValue").GetInt32()));
        }
        var ofB = await DashboardAsync(app, app.AdultB, "me");
        Assert.Equal(("388000.00", 0), (Total(ofB.GetProperty("netWorth")), ofB.GetProperty("envelopesWithoutValue").GetInt32()));
    }
}
