using Nid.Core.Contracts;
using Nid.Platform.Import;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Import;

public class ImportStagingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

        Assert.NotNull(await store.CreateAsync(a, NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken));
        Assert.Null(await store.CreateAsync(a, NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_session_is_found_only_by_its_owner()
    {
        var store = await NewStoreAsync();
        var a = PersonId.New();
        var session = (await store.CreateAsync(a, new Dictionary<string, PersonId> { ["adult-1"] = a }, Now, Tomorrow, TestContext.Current.CancellationToken))!;

        var found = await store.FindOwnedAsync(session.Id, a, TestContext.Current.CancellationToken);
        Assert.Equal(a, found!.Adults["adult-1"]);
        Assert.Null(await store.FindOwnedAsync(session.Id, PersonId.New(), TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListOwnedAsync(PersonId.New(), 20, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_transition_happens_only_from_an_expected_state()
    {
        var store = await NewStoreAsync();
        var session = (await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken))!;

        Assert.False(await store.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, Now, Tomorrow, null, TestContext.Current.CancellationToken));
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Validated, Now, Tomorrow, "{}", TestContext.Current.CancellationToken));
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, Now, Tomorrow, null, TestContext.Current.CancellationToken));
        Assert.False(await store.TransitionAsync(session.Id, [ImportState.Validated], ImportState.Committed, Now, Tomorrow, null, TestContext.Current.CancellationToken));
    }

    /// <summary>updated_at is the row's version: it moves on every transition, even two at the same instant.</summary>
    [Fact]
    public async Task A_transition_given_a_stale_version_does_not_happen()
    {
        var store = await NewStoreAsync();
        var a = PersonId.New();
        var session = (await store.CreateAsync(a, NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken))!;

        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Open, Now, Tomorrow, null, TestContext.Current.CancellationToken));
        var moved = (await store.FindOwnedAsync(session.Id, a, TestContext.Current.CancellationToken))!;

        Assert.True(moved.UpdatedAt > session.UpdatedAt);
        Assert.False(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Validated, Now, Tomorrow, "{}", TestContext.Current.CancellationToken,
            expectedUpdatedAt: session.UpdatedAt));
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Validated, Now, Tomorrow, "{}", TestContext.Current.CancellationToken,
            expectedUpdatedAt: moved.UpdatedAt));
    }

    [Fact]
    public async Task A_batch_slot_is_taken_once()
    {
        var store = await NewStoreAsync();
        var session = (await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken))!;

        Assert.True(await store.AddBatchAsync(session.Id, new StoredBatch("persons", 0, new string('a', 64), 1, "[{}]"), Now, TestContext.Current.CancellationToken));
        Assert.False(await store.AddBatchAsync(session.Id, new StoredBatch("persons", 0, new string('b', 64), 1, "[{}]"), Now, TestContext.Current.CancellationToken));

        var batch = await store.FindBatchAsync(session.Id, "persons", 0, TestContext.Current.CancellationToken);
        Assert.Equal(new string('a', 64), batch!.Hash);
        Assert.Equal(1, await store.EntityCountAsync(session.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Expiry_frees_the_person_and_empties_the_staging()
    {
        var store = await NewStoreAsync();
        var a = PersonId.New();
        var stale = (await store.CreateAsync(a, NoAdults, Now, Now.Plus(NodaTime.Duration.FromMinutes(1)), TestContext.Current.CancellationToken))!;
        await store.AddBatchAsync(stale.Id, new StoredBatch("persons", 0, new string('a', 64), 1, "[{}]"), Now, TestContext.Current.CancellationToken);

        await store.ExpireAsync(Now.Plus(NodaTime.Duration.FromMinutes(2)), TestContext.Current.CancellationToken);

        Assert.Equal(ImportState.Expired, (await store.FindOwnedAsync(stale.Id, a, TestContext.Current.CancellationToken))!.State);
        Assert.Empty(await store.BatchesAsync(stale.Id, TestContext.Current.CancellationToken));
        Assert.NotNull(await store.CreateAsync(a, NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken));
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
        var session = (await store.CreateAsync(PersonId.New(), NoAdults, Now, Tomorrow, TestContext.Current.CancellationToken))!;
        Assert.True(await store.TransitionAsync(session.Id, [ImportState.Open], ImportState.Cancelled, Now, Tomorrow, null, TestContext.Current.CancellationToken));
        await store.AddBatchAsync(session.Id, new StoredBatch("persons", 0, new string('a', 64), 1, "[{}]"), Now, TestContext.Current.CancellationToken);

        await store.ExpireAsync(Now, TestContext.Current.CancellationToken);

        Assert.Empty(await store.BatchesAsync(session.Id, TestContext.Current.CancellationToken));
    }
}
