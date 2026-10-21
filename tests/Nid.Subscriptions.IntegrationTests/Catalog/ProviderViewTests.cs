using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>Counts the calls made through an interface: how the one-query-per-list rule is observed.</summary>
public class CountingProxy<T> : DispatchProxy where T : class
{
    public T Inner { get; set; } = null!;

    public ConcurrentDictionary<string, int> Calls { get; set; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls.AddOrUpdate(targetMethod!.Name, 1, (_, count) => count + 1);
        try
        {
            return targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

public class ProviderViewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record LabelResponse(string Key, string Name, string Color);
    public sealed record ProviderResponse(string Key, string Name, string Origin, List<LabelResponse> Labels);
    public sealed record RowResponse(Guid Id, string DisplayName, ProviderResponse? Provider);
    public sealed record ListResponse(List<RowResponse> Items);
    public sealed record DetailResponse(Guid Id, ProviderResponse? Provider);

    private static async Task SyncRealCatalogAsync(NidApp app)
    {
        app.Catalog.Serve(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders());
        var (run, failure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());
        Assert.Null(failure);
        Assert.Equal("succeeded", ((CatalogSyncRunView)run!).Status);
    }

    private static async Task<Guid> CreateAsync(NidApp app, string name, string? providerKey)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = name,
            visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            providerKey,
            autoRenew = true,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static void AssertNetflix(ProviderResponse? provider)
    {
        Assert.NotNull(provider);
        Assert.Equal(("netflix", "Netflix", "catalog"), (provider.Key, provider.Name, provider.Origin));
        Assert.Equal([new LabelResponse("streaming", "Streaming", "#6400F6"), new LabelResponse("vod", "VOD", "#F06292")], provider.Labels);
    }

    [Fact]
    public async Task A_row_and_a_detail_carry_their_provider_with_its_labels()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SyncRealCatalogAsync(app);
        var id = await CreateAsync(app, "Netflix Standard", "netflix");

        var list = await app.GetAsync<ListResponse>(app.AdultB, "/api/v1/subscriptions");
        AssertNetflix(Assert.Single(list.Items, i => i.Id == id).Provider);

        var detail = await app.GetAsync<DetailResponse>(app.AdultB, $"/api/v1/subscriptions/{id}");
        AssertNetflix(detail.Provider);
    }

    [Fact]
    public async Task A_subscription_without_a_provider_has_none()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, "Something", null);

        Assert.Null((await app.GetAsync<DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{id}")).Provider);
        Assert.Contains("\"provider\":null", await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{id}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_archived_provider_is_still_shown_on_its_subscription()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SyncRealCatalogAsync(app);
        var id = await CreateAsync(app, "Netflix Standard", "netflix");
        await using (var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString))
            await subs.Providers.Where(p => p.Key == "netflix")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, (NodaTime.Instant?)NodaTime.SystemClock.Instance.GetCurrentInstant()));

        AssertNetflix((await app.GetAsync<DetailResponse>(app.AdultA, $"/api/v1/subscriptions/{id}")).Provider);
    }

    [Fact]
    public async Task A_list_loads_its_providers_in_one_query_whatever_their_number()
    {
        var calls = new ConcurrentDictionary<string, int>();
        await using var app = await NidApp.StartAsync(fixture, services => services.AddScoped<IProviderStore>(sp =>
        {
            var proxy = DispatchProxy.Create<IProviderStore, CountingProxy<IProviderStore>>();
            var counting = (CountingProxy<IProviderStore>)(object)proxy;
            counting.Inner = new ProviderStore(sp.GetRequiredService<SubscriptionsDbContext>());
            counting.Calls = calls;
            return proxy;
        }));
        await SyncRealCatalogAsync(app);
        foreach (var key in new[] { "netflix", "spotify", "nordvpn" })
            await CreateAsync(app, key, key);

        calls.Clear();
        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions");

        Assert.Equal(3, list.Items.Count(i => i.Provider is not null));
        Assert.Equal(1, calls.GetValueOrDefault(nameof(IProviderStore.FindAsync)));
        Assert.Equal(1, calls.GetValueOrDefault(nameof(IProviderStore.LabelsAsync)));
        Assert.Equal(2, calls.Values.Sum());
    }
}
