using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.Platform.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;
using NodaTime;

namespace Nid.Host.IntegrationTests.Import;

/// <summary>
/// A batch PUT overlapping a validate and a commit (final review of 1D1, finding 1). The PUT is held between
/// moving the session to open and inserting its row; validate and commit run meanwhile. Whatever they do, a
/// batch answered <c>Accepted: true</c> is either applied by the commit or still staged on a session that can
/// be validated and committed again — never accepted and then silently dropped.
/// </summary>
public class BatchRaceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_batch_racing_validate_and_commit_is_applied_or_refused_never_accepted_then_lost()
    {
        var gates = new Gates();
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddScoped<IImportSection, StubSection>();
            services.AddScoped<IImportStaging>(sp => new PausingStaging(ActivatorUtilities.CreateInstance<ImportStagingStore>(sp), gates));
        });
        var client = new ImportClient(app);
        var session = await client.StartAsync(app.AdultA);

        // 1. The PUT moves the session to open, then stops before inserting its batch.
        gates.Armed = true;
        var put = client.PutRawAsync(app.AdultA, session.Id, StubSection.Name, 0, """[{"id":"late"}]""");
        await gates.Transitioned.Task.WaitAsync(Bound);

        // 2. A validation starts meanwhile. Either it runs to the end (nothing holds the session), or it waits
        //    on the PUT's hold of the session row — whichever happens first is observed, not assumed.
        var validate = client.ValidateRawAsync(app.AdultA, session.Id);
        var validateBlocked = await FinishedOrWaitingOnALockAsync(app.ConnectionString, validate);

        // 3. A commit runs while the PUT is still held (it cannot wait on the PUT: it moves only a validated
        //    session, and a session the PUT holds is not validated in any committed state it can see).
        var commit = await client.CommitRawAsync(app.AdultA, session.Id).WaitAsync(Bound);

        // 4. The PUT goes on and inserts its batch.
        gates.Release.SetResult();
        var putResponse = await put.WaitAsync(Bound);
        var validateResponse = await validate.WaitAsync(Bound);

        Assert.True(putResponse.IsSuccessStatusCode, await putResponse.Content.ReadAsStringAsync());
        var batch = (await putResponse.Content.ReadFromJsonAsync<BatchResponse>(ImportClient.Json))!;
        var after = await client.GetAsync(app.AdultA, session.Id);
        await using var platform = PostgresFixture.NewPlatformContext(app.ConnectionString);
        var staged = await platform.Batches.AnyAsync(b => b.SessionId == session.Id);

        if (batch.Accepted)
        {
            var applied = after.State == "committed" && after.Report!.Sections.TryGetValue(StubSection.Name, out var counts) && counts.Created == 1;
            var stillPending = staged && ImportStates.Active.Select(ImportStates.Name).Contains(after.State);
            Assert.True(applied || stillPending,
                $"Accepted, then lost: session {after.State}, staged {staged}, commit {commit.StatusCode}, validate {validateResponse.StatusCode}.");
        }

        // With the session held across the move and the insert, the validation that started meanwhile waits,
        // then finds the session changed under it and refuses to store its stale report.
        Assert.True(validateBlocked, "The validation was not held by the PUT.");
        Assert.Equal(HttpStatusCode.Conflict, validateResponse.StatusCode);
        Assert.Equal(ImportCodes.State, await ImportClient.CodeOf(validateResponse));
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
        Assert.Equal("open", after.State);
    }

    /// <summary>True when a backend of this database is waiting on a lock before <paramref name="request"/> ends.</summary>
    private static async Task<bool> FinishedOrWaitingOnALockAsync(string connectionString, Task request)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow + Bound;
        while (DateTime.UtcNow < deadline)
        {
            if (request.IsCompleted)
                return false;
            await using var command = new NpgsqlCommand(
                "select count(*) from pg_stat_activity where datname = current_database() and wait_event_type = 'Lock'", connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
                return true;
            await Task.Yield();
        }

        throw new TimeoutException("The validation neither finished nor waited on a lock.");
    }

    private sealed class Gates
    {
        public volatile bool Armed;
        public TaskCompletionSource Transitioned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Holds the one armed PUT right after its move to open, until the test releases it.</summary>
    private sealed class PausingStaging(IImportStaging inner, Gates gates) : IImportStaging
    {
        public async Task<bool> TransitionAsync(Guid session, IReadOnlyCollection<ImportState> from, ImportState to, Instant now, Instant expiresAt,
            string? reportJson, CancellationToken ct, Instant? expectedUpdatedAt = null)
        {
            var moved = await inner.TransitionAsync(session, from, to, now, expiresAt, reportJson, ct, expectedUpdatedAt);
            if (to == ImportState.Open && gates.Armed)
            {
                gates.Armed = false;
                gates.Transitioned.SetResult();
                await gates.Release.Task.WaitAsync(Bound, ct);
            }
            return moved;
        }

        public Task ExpireAsync(Instant now, CancellationToken ct) => inner.ExpireAsync(now, ct);
        public Task<ImportSession?> CreateAsync(PersonId person, IReadOnlyDictionary<string, PersonId> adults, Instant now, Instant expiresAt, CancellationToken ct)
            => inner.CreateAsync(person, adults, now, expiresAt, ct);
        public Task<ImportSession?> FindOwnedAsync(Guid id, PersonId person, CancellationToken ct) => inner.FindOwnedAsync(id, person, ct);
        public Task<IReadOnlyList<ImportSession>> ListOwnedAsync(PersonId person, int limit, CancellationToken ct) => inner.ListOwnedAsync(person, limit, ct);
        public Task<StoredBatch?> FindBatchAsync(Guid session, string section, int index, CancellationToken ct) => inner.FindBatchAsync(session, section, index, ct);
        public Task<IReadOnlyList<StoredBatch>> BatchesAsync(Guid session, CancellationToken ct) => inner.BatchesAsync(session, ct);
        public Task<int> EntityCountAsync(Guid session, CancellationToken ct) => inner.EntityCountAsync(session, ct);
        public Task<bool> AddBatchAsync(Guid session, StoredBatch batch, Instant now, CancellationToken ct) => inner.AddBatchAsync(session, batch, now, ct);
        public Task ClearBatchesAsync(Guid session, CancellationToken ct) => inner.ClearBatchesAsync(session, ct);
    }
}
