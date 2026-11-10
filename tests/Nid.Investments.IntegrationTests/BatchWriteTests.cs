using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.Investments.IntegrationTests;

/// <summary>
/// The commands a statement applies through, several lines as one write: each line checked as its one-line command checks it,
/// a refusal naming its line by index and leaving nothing written.
/// </summary>
public class BatchWriteTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate On = new(2025, 9, 15);

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Ok();

    private static HoldingDraft Holding(string name, string kind, string? isin = null)
        => new(name, isin, kind, null, null, null, null, null, null, null, null);

    private static MovementDraft Movement(string kind, decimal amount, decimal? units = null)
        => new(On, kind, units, null, Eur(amount), null, null, null);

    private static async Task<JsonElement> HoldingsAsync(NidApp app, ItemId envelope)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{envelope.Value}/holdings")).RootElement;

    private static async Task<int> MovementCountAsync(NidApp app, ItemId envelope, Guid holding)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, $"/api/v1/investments/envelopes/{envelope.Value}/holdings/{holding}/movements"))
            .RootElement.GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Holdings_are_added_together_and_an_isin_held_twice_refuses_them_all_at_the_second()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);

        var (_, refused) = await app.InvokeAsync(app.AdultA, new AddHoldingsCommand(envelope,
            [Holding("Fonds A", "fund", "FR0000120271"), Holding("Fonds A bis", "fund", "FR0000120271")]), TestContext.Current.CancellationToken);
        Assert.Equal(1, Batch.IndexOf(refused!));
        Assert.Empty(EnumerateHoldings(await HoldingsAsync(app, envelope)));

        var (added, failure) = await app.InvokeAsync(app.AdultA, new AddHoldingsCommand(envelope,
            [Holding("Fonds euros", "euro_fund"), Holding("Fonds A", "fund", "FR0000120271")]), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        var ids = Assert.IsAssignableFrom<IReadOnlyList<Guid>>(added);
        Assert.Equal(ids.Order(), EnumerateHoldings(await HoldingsAsync(app, envelope)).Select(h => h.GetProperty("id").GetGuid()).Order());
    }

    [Fact]
    public async Task Movements_are_written_together_and_a_refused_one_names_its_index_and_writes_none()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, Visibility.Shared);
        var (added, _) = await app.InvokeAsync(app.AdultA, new AddHoldingsCommand(envelope,
            [Holding("Fonds euros", "euro_fund"), Holding("Fonds A", "fund")]), TestContext.Current.CancellationToken);
        var holdings = (IReadOnlyList<Guid>)added!;
        var (euro, fund) = (holdings[0], holdings[1]);

        // A euro fund takes no units: the second movement is refused, the first one is not kept.
        var (_, refused) = await app.InvokeAsync(app.AdultA, new AddHoldingMovementsCommand(envelope,
            [new HoldingMovementLine(fund, Movement("allocation_in", 100m, 1m)), new HoldingMovementLine(euro, Movement("allocation_in", 50m, 1m))]),
            TestContext.Current.CancellationToken);
        Assert.Equal(("investments.invalid_movement", 1), (refused!.Code, Batch.IndexOf(refused)));
        Assert.Equal(0, await MovementCountAsync(app, envelope, fund));

        // Another envelope's holding is absent, as for one movement.
        var (_, absent) = await app.InvokeAsync(app.AdultA, new AddHoldingMovementsCommand(envelope,
            [new HoldingMovementLine(Guid.NewGuid(), Movement("fee", 1m))]), TestContext.Current.CancellationToken);
        Assert.IsType<NotFoundFailure>(absent);

        var (written, failure) = await app.InvokeAsync(app.AdultA, new AddHoldingMovementsCommand(envelope,
            [new HoldingMovementLine(fund, Movement("allocation_in", 100m, 1m)), new HoldingMovementLine(euro, Movement("interest", 5m)),
             new HoldingMovementLine(fund, Movement("fee", 2m, -0.02m))]), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        Assert.Equal(3, ((IReadOnlyList<Guid>)written!).Count);
        Assert.Equal(2, await MovementCountAsync(app, envelope, fund));
        Assert.Equal(1, await MovementCountAsync(app, envelope, euro));
    }

    private static IEnumerable<JsonElement> EnumerateHoldings(JsonElement holdings)
        => holdings.GetProperty("holdings").EnumerateArray();
}
