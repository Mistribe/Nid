using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Investments.IntegrationTests;

public class SecurityBreakdownStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string X = "FR0000000001", Y = "FR0000000002";
    private static readonly PersonId Person = PersonId.New();

    private static SecurityBreakdown Make(string isin, LocalDate asOf, string region = "france", bool sectors = true, Guid? id = null)
        => new(id ?? Guid.NewGuid(), isin, asOf, [new BreakdownPart(region, 100m)], sectors ? [new BreakdownPart("financials", 100m)] : null,
            null, Person, Instant.FromUtc(2026, 11, 2, 10, 0));

    [Fact]
    public async Task Latest_is_the_most_recent_version_per_isin()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>();
        var ct = TestContext.Current.CancellationToken;
        await store.WriteAsync(Make(X, new LocalDate(2025, 1, 1), "japan"), ct);
        await store.WriteAsync(Make(X, new LocalDate(2026, 1, 1), "france"), ct);
        await store.WriteAsync(Make(Y, new LocalDate(2024, 1, 1), "emerging"), ct);

        var latest = await store.LatestAsync([X, Y, "FR0000000003"], ct);

        Assert.Equal(2, latest.Count);
        Assert.Equal(new LocalDate(2026, 1, 1), latest[X].AsOf);
        Assert.Equal("france", latest[X].Regions.Single().Code);
        Assert.Equal(new LocalDate(2024, 1, 1), latest[Y].AsOf);
        Assert.Equal(3, (await store.AllAsync([X, Y], ct)).Count);
    }

    [Fact]
    public async Task Writing_the_same_isin_and_date_replaces_it_and_keeps_its_id()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>();
        var ct = TestContext.Current.CancellationToken;
        var day = new LocalDate(2026, 1, 1);
        var first = await store.WriteAsync(Make(X, day, "japan"), ct);
        var second = await store.WriteAsync(Make(X, day, "france"), ct);

        Assert.Equal(first, second);
        var all = await store.AllAsync([X], ct);
        Assert.Equal("france", Assert.Single(all).Regions.Single().Code);
    }

    [Fact]
    public async Task Sectors_are_optional()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>();
        var ct = TestContext.Current.CancellationToken;
        await store.WriteAsync(Make(X, new LocalDate(2026, 1, 1), sectors: false), ct);

        Assert.Null((await store.LatestAsync([X], ct))[X].Sectors);
    }

    [Fact]
    public async Task Delete_removes_one_version()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>();
        var ct = TestContext.Current.CancellationToken;
        await store.WriteAsync(Make(X, new LocalDate(2025, 1, 1)), ct);
        await store.WriteAsync(Make(X, new LocalDate(2026, 1, 1)), ct);

        Assert.True(await store.DeleteAsync(X, new LocalDate(2026, 1, 1), ct));
        Assert.False(await store.DeleteAsync(X, new LocalDate(2026, 1, 1), ct));
        Assert.Equal(new LocalDate(2025, 1, 1), (await store.LatestAsync([X], ct))[X].AsOf);
    }

    [Fact]
    public async Task Stamping_sets_the_document_on_the_given_breakdowns_only_and_ignores_unknown_ids()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecurityBreakdownStore>();
        var ct = TestContext.Current.CancellationToken;
        var document = Guid.NewGuid();
        var stamped = await store.WriteAsync(Make(X, new LocalDate(2026, 1, 1)), ct);
        var untouched = await store.WriteAsync(Make(Y, new LocalDate(2026, 1, 1)), ct);

        await store.StampDocumentAsync(document, [stamped, Guid.NewGuid()], ct);

        var all = (await store.AllAsync([X, Y], ct)).ToDictionary(b => b.Id);
        Assert.Equal(document, all[stamped].DocumentId);
        Assert.Null(all[untouched].DocumentId);
    }
}
