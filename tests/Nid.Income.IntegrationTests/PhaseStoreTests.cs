using Nid.Core.Contracts;
using Nid.Income.Contracts;
using Nid.Income.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Income.IntegrationTests;

public class PhaseStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static IncomePhase Phase(ItemId source, string label)
        => new(Guid.CreateVersion7(), source, label, new LocalDate(2025, 1, 1), null,
            Money.Create(2000m, Currency.Eur).TryGetValue(out var net) ? net : throw new InvalidOperationException(),
            Duration.Create(1, DurationUnit.Month).TryGetValue(out var every) ? every : throw new InvalidOperationException(), null, null, null);

    [Fact]
    public async Task Find_many_reads_phases_of_several_sources_by_id_and_skips_unknown_ids()
    {
        await using var income = PostgresFixture.NewIncomeContext(await fixture.NewConnectionStringAsync());
        var store = new PhaseStore(income);
        var a = await store.AddAsync(Phase(new ItemId(Guid.CreateVersion7()), "A"), null, default);
        var b = await store.AddAsync(Phase(new ItemId(Guid.CreateVersion7()), "B"), null, default);
        await store.AddAsync(Phase(new ItemId(Guid.CreateVersion7()), "C"), null, default);

        var found = await store.FindManyAsync([a.Id, b.Id, Guid.NewGuid()], default);

        Assert.Equal(new[] { a.Id, b.Id }.Order(), found.Select(p => p.Id).Order());
        Assert.Empty(await store.FindManyAsync([], default));
    }
}
