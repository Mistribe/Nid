using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

/// <summary>Batched reads spec §2–§3: the net worth reads its facts as one block per module, and only the reader's items.</summary>
[Collection("postgres")]
public class BatchedNetWorthTests(PostgresFixture fixture)
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
}
