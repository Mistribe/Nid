using Nid.Core.Contracts;
using Nid.Platform.Import;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class ImportStagingTests(PostgresFixture fixture)
{
    private static readonly Instant Now = Instant.FromUtc(2025, 10, 15, 12, 0);
    private static readonly Instant Tomorrow = Now.Plus(NodaTime.Duration.FromHours(24));
    private static readonly Dictionary<string, PersonId> NoAdults = [];

    private async Task<ImportStagingStore> NewStoreAsync()
        => new(PostgresFixture.NewPlatformContext(await fixture.NewConnectionStringAsync()));

    [Fact]
    public async Task A_person_has_at_most_one_active_session()
    {
        var store = await NewStoreAsync();
        var a = PersonId.New();

        Assert.NotNull(await store.CreateAsync(a, NoAdults, Now, Tomorrow, default));
        Assert.Null(await store.CreateAsync(a, NoAdults, Now, Tomorrow, default));
        Assert.NotNull(await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, default));
    }

    [Fact]
    public async Task A_session_is_found_only_by_its_owner()
    {
        var store = await NewStoreAsync();
        var a = PersonId.New();
        var session = (await store.CreateAsync(a, new Dictionary<string, PersonId> { ["adult-1"] = a }, Now, Tomorrow, default))!;

        var found = await store.FindOwnedAsync(session.Id, a, default);
        Assert.Equal(a, found!.Adults["adult-1"]);
        Assert.Null(await store.FindOwnedAsync(session.Id, PersonId.New(), default));
        Assert.Empty(await store.ListOwnedAsync(PersonId.New(), 20, default));
    }

    [Fact]
    public async Task A_transition_happens_only_from_an_expected_state()
    {
        var store = await NewStoreAsync();
        var session = (await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, default))!;

        Assert.False(await store.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, Now, Tomorrow, null, default));
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Validated, Now, Tomorrow, "{}", default));
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, Now, Tomorrow, null, default));
        Assert.False(await store.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, Now, Tomorrow, null, default));
    }

    [Fact]
    public async Task A_batch_slot_is_taken_once()
    {
        var store = await NewStoreAsync();
        var session = (await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, default))!;

        Assert.True(await store.AddBatchAsync(session.Id, new StoredBatch("persons", 0, new string('a', 64), 1, "[{}]"), Now, default));
        Assert.False(await store.AddBatchAsync(session.Id, new StoredBatch("persons", 0, new string('b', 64), 1, "[{}]"), Now, default));

        var batch = await store.FindBatchAsync(session.Id, "persons", 0, default);
        Assert.Equal(new string('a', 64), batch!.Hash);
        Assert.Equal(1, await store.EntityCountAsync(session.Id, default));
    }

    [Fact]
    public async Task Expiry_frees_the_person_and_empties_the_staging()
    {
        var store = await NewStoreAsync();
        var a = PersonId.New();
        var stale = (await store.CreateAsync(a, NoAdults, Now, Now.Plus(NodaTime.Duration.FromMinutes(1)), default))!;
        await store.AddBatchAsync(stale.Id, new StoredBatch("persons", 0, new string('a', 64), 1, "[{}]"), Now, default);

        await store.ExpireAsync(Now.Plus(NodaTime.Duration.FromMinutes(2)), default);

        Assert.Equal(ImportState.Expired, (await store.FindOwnedAsync(stale.Id, a, default))!.State);
        Assert.Empty(await store.BatchesAsync(stale.Id, default));
        Assert.NotNull(await store.CreateAsync(a, NoAdults, Now, Tomorrow, default));
    }

    /// <summary>
    /// Self-healing (§11.5): staging of a non-active session is never needed, so ExpireAsync also sweeps
    /// batches left on a session that is already done — cancelled here, not merely one it just expired itself —
    /// covering a batch that slipped in concurrently between marking a session done and clearing its batches.
    /// </summary>
    [Fact]
    public async Task Expiry_also_clears_batches_left_on_an_already_cancelled_session()
    {
        var store = await NewStoreAsync();
        var session = (await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, default))!;
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Cancelled, Now, Tomorrow, null, default));
        await store.AddBatchAsync(session.Id, new StoredBatch("persons", 0, new string('a', 64), 1, "[{}]"), Now, default);

        await store.ExpireAsync(Now, default);

        Assert.Empty(await store.BatchesAsync(session.Id, default));
    }
}
