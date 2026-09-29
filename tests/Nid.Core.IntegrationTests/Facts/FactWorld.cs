using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests.Facts;

/// <summary>
/// Alice and Bob, a clock at <see cref="Now"/> (2025-06-30 in Paris), and stores acting as <see cref="Act"/> says.
/// <see cref="ForSameDatabase"/> opens a second world on the same database, for the race tests.
/// </summary>
internal sealed class FactWorld : IAsyncDisposable
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    public static readonly Instant Now = Instant.FromUtc(2025, 6, 30, 10, 0);
    public static readonly LocalDate Today = new(2025, 6, 30);

    private readonly ActorAccessor actors = new();

    private FactWorld(string connectionString, PersonId alice, PersonId bob)
    {
        ConnectionString = connectionString;
        Db = PostgresFixture.NewCoreContext(connectionString);
        Alice = alice;
        Bob = bob;
        var clock = new FixedClock(Now);
        var persons = new PersonRegistry(Db, clock);
        Items = new ItemRegistry(Db, clock);
        var gate = new FactGate(Db, new AccessPolicy(Items, persons), actors, new HouseholdCalendar(Db, clock));
        Valuations = new Valuations(Db, gate, clock);
        Flows = new Flows(Db, gate, clock);
        Act(alice);
    }

    public string ConnectionString { get; }
    public CoreDbContext Db { get; }
    public PersonId Alice { get; }
    public PersonId Bob { get; }
    public ItemRegistry Items { get; }
    public Valuations Valuations { get; }
    public Flows Flows { get; }

    public static async Task<FactWorld> NewAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewCoreContext(connectionString);
        var persons = new PersonRegistry(db, PostgresFixture.Clock);
        Assert.True((await persons.ProvisionAdultAsync("sub-a", "Alice", null, default)).TryGetValue(out var alice));
        Assert.True((await persons.PreRegisterAdultAsync("Bob", "bob@example.test", default)).IsSuccess);
        Assert.True((await persons.ProvisionAdultAsync("sub-b", "Bob", "bob@example.test", default)).TryGetValue(out var bob));
        return new FactWorld(connectionString, alice.Id, bob.Id);
    }

    public FactWorld ForSameDatabase() => new(ConnectionString, Alice, Bob);

    public void Act(PersonId person) => actors.Current = Actor.Browser(person);

    public void ActAs(Actor actor) => actors.Current = actor;

    public async Task<ItemId> ItemAsync(PersonId owner, Visibility visibility = Visibility.Private, string? currency = "EUR")
        => (await Items.CreateAsync(new NewItem("test", "envelope", "PEA", visibility, currency, [new OwnerShare(owner, 10_000)]), default)).Ok();

    public static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).Ok();

    public static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd).Ok();

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
