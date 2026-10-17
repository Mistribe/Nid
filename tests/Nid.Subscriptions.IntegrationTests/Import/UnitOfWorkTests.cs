using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
using Npgsql;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.Import;

[Collection("postgres")]
public class UnitOfWorkTests(PostgresFixture fixture)
{
    /// <summary>The Core item and the subscription row are one write: the second failing takes the first back.</summary>
    [Fact]
    public async Task Creating_a_subscription_whose_row_fails_leaves_no_orphan_item()
    {
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddScoped<ISubscriptionStore>(sp => new FailingCreateStore(
                ActivatorUtilities.CreateInstance<Nid.Subscriptions.Infrastructure.Persistence.SubscriptionStore>(sp))));

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = "orphan-candidate",
            visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
            autoRenew = true,
        });

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        Assert.False(await core.Items.AnyAsync(i => i.DisplayName == "orphan-candidate"));
    }

    [Fact]
    public async Task A_committed_scope_keeps_the_writes_of_both_modules()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var id = await app.NewSubscriptionAsync("kept", app.AdultA, Visibility.Shared);

        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        await using var subs = PostgresFixture.NewSubscriptionsContext(app.ConnectionString);
        Assert.True(await core.Items.AnyAsync(i => i.Id == id.Value));
        Assert.True(await subs.Subscriptions.AnyAsync(s => s.ItemId == id.Value));
    }

    /// <summary>Spec 4D1 §5: an inner scope joins the outer one; only the outer commit makes its writes durable.</summary>
    [Fact]
    public async Task A_scope_inside_another_joins_it_and_lasts_only_if_the_outer_commits()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProbeAsync(app);

        foreach (var outerCommits in new[] { false, true })
        {
            await using var scope = app.Services.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();
            var core = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            await using (var outer = await unitOfWork.BeginAsync(default))
            {
                await using (var inner = await unitOfWork.BeginAsync(default))
                {
                    await core.Database.ExecuteSqlRawAsync(outerCommits
                        ? "insert into nest_probe (id) values (2)"
                        : "insert into nest_probe (id) values (1)");
                    await inner.CommitAsync(default);
                }
                if (outerCommits)
                    await outer.CommitAsync(default);
            }
        }

        Assert.Equal([2], await ProbedAsync(app));
    }

    // Review Focus 5.
    [Fact]
    public async Task An_inner_scope_disposed_without_a_commit_undoes_only_its_own_writes()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProbeAsync(app);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();
            var core = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            await using var outer = await unitOfWork.BeginAsync(default);
            await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (1)");
            await using (var abandoned = await unitOfWork.BeginAsync(default))
                await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (2)");
            // The next inner scope opens cleanly after one rolled back.
            await using (var again = await unitOfWork.BeginAsync(default))
            {
                await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (3)");
                await again.CommitAsync(default);
            }
            await outer.CommitAsync(default);
        }

        Assert.Equal([1, 3], await ProbedAsync(app));
    }

    // Final fix 9: a savepoint disposed twice rolls back once; a second rollback would undo the outer scope's later writes.
    [Fact]
    public async Task An_inner_scope_disposed_twice_does_nothing_the_second_time()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProbeAsync(app);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();
            var core = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            await using var outer = await unitOfWork.BeginAsync(default);
            var inner = await unitOfWork.BeginAsync(default);
            await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (1)");
            await inner.DisposeAsync();
            await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (2)");
            await inner.DisposeAsync();
            Assert.Equal(0, (int)unitOfWork.GetType().GetField("depth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(unitOfWork)!);
            await outer.CommitAsync(default);
        }

        Assert.Equal([2], await ProbedAsync(app));
    }

    // An inner scope that cannot roll back to its savepoint may leave its writes behind: the outer commit is refused.
    [Fact]
    public async Task An_inner_scope_that_cannot_roll_back_makes_the_outer_commit_fail_then_the_next_unit_of_work_starts_clean()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProbeAsync(app);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();
            var core = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            await using (var outer = await unitOfWork.BeginAsync(default))
            {
                var inner = await unitOfWork.BeginAsync(default);
                await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (1)");
                await core.Database.ExecuteSqlRawAsync("release savepoint uow_1"); // the rollback to it now fails
                await inner.DisposeAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => outer.CommitAsync(default));
            }

            await using var next = await unitOfWork.BeginAsync(default);
            await core.Database.ExecuteSqlRawAsync("insert into nest_probe (id) values (3)");
            await next.CommitAsync(default);
        }

        Assert.Equal([3], await ProbedAsync(app));
    }

    /// <summary>A table of its own, created outside any unit of work.</summary>
    private static async Task ProbeAsync(NidApp app)
    {
        await using var setup = PostgresFixture.NewCoreContext(app.ConnectionString);
        await setup.Database.ExecuteSqlRawAsync("create table nest_probe (id int)");
    }

    private static async Task<List<int>> ProbedAsync(NidApp app)
    {
        await using var read = PostgresFixture.NewCoreContext(app.ConnectionString);
        return await read.Database.SqlQueryRaw<int>("select id as \"Value\" from nest_probe order by id").ToListAsync();
    }

    /// <summary>
    /// A commit can fail on its own (a serialization failure, a deferred constraint, a dropped connection).
    /// Npgsql marks the ADO.NET transaction completed the instant a commit is attempted, whether it succeeds
    /// or not, so <c>Scope.DisposeAsync</c>'s own <c>RollbackAsync</c> used to throw
    /// <see cref="InvalidOperationException"/> ("This NpgsqlTransaction has completed") and replace the real
    /// failure. A <c>DEFERRABLE INITIALLY DEFERRED</c> unique constraint is the deterministic way to make
    /// <c>CommitAsync</c> itself fail: Postgres lets the duplicate rows exist until commit, so the violation
    /// surfaces there rather than on the earlier <c>INSERT</c>s.
    /// </summary>
    [Fact]
    public async Task A_failing_commit_surfaces_its_own_exception_not_the_rollbacks()
    {
        await using var app = await NidApp.StartAsync(fixture);

        // Created outside the unit of work, over its own connection, so the table exists independently of
        // the transaction under test.
        await using (var setup = PostgresFixture.NewCoreContext(app.ConnectionString))
        {
            await setup.Database.ExecuteSqlRawAsync(
                "create table commit_fail_probe (id int, constraint uq_commit_fail_probe unique (id) deferrable initially deferred)");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();
        var core = scope.ServiceProvider.GetRequiredService<CoreDbContext>();

        var transaction = await unitOfWork.BeginAsync(default);
        await core.Database.ExecuteSqlRawAsync("insert into commit_fail_probe (id) values (1)");
        await core.Database.ExecuteSqlRawAsync("insert into commit_fail_probe (id) values (1)");

        var failure = await Record.ExceptionAsync(() => transaction.CommitAsync(default));
        Assert.IsType<PostgresException>(failure);

        // Disposing after the failed commit must not replace that failure with a rollback failure of its
        // own; if it did, this DisposeAsync call would throw InvalidOperationException instead of returning.
        await transaction.DisposeAsync();
    }

    /// <summary>
    /// A context that fails to enlist must not leave the transaction begun, nor the contexts enlisted before it
    /// pointing at it: the next unit of work on the same connection starts clean.
    /// </summary>
    [Fact]
    public async Task A_context_failing_to_enlist_leaves_nothing_begun()
    {
        var failing = new FailingOnceParticipant();
        await using var app = await NidApp.StartAsync(fixture, services =>
            services.AddScoped<Nid.Platform.Infrastructure.Persistence.ITransactionParticipant>(_ => failing));
        await using var scope = app.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();
        var core = scope.ServiceProvider.GetRequiredService<CoreDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.BeginAsync(default));

        Assert.Null(core.Database.CurrentTransaction);
        await using var again = await unitOfWork.BeginAsync(default);
        Assert.NotNull(core.Database.CurrentTransaction);
    }

    private sealed class FailingOnceParticipant : Nid.Platform.Infrastructure.Persistence.ITransactionParticipant
    {
        private bool failed;

        public Task EnlistAsync(System.Data.Common.DbTransaction? transaction, CancellationToken ct)
        {
            if (transaction is null || failed)
                return Task.CompletedTask;
            failed = true;
            throw new InvalidOperationException("Refused to enlist on purpose.");
        }
    }

    /// <summary>Delegates everything, and refuses creation after the Core item already exists.</summary>
    private sealed class FailingCreateStore(ISubscriptionStore inner) : ISubscriptionStore
    {
        public Task<Result> CreateAsync(ItemId id, NewSubscription subscription, CancellationToken ct)
            => Task.FromResult(Result.Failure(new Failure("test.create_refused", "Refused on purpose.", null)));

        public Task<SubscriptionInput?> LoadAsync(ItemId id, CancellationToken ct) => inner.LoadAsync(id, ct);
        public Task<IReadOnlyList<SubscriptionInput>> LoadManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct) => inner.LoadManyAsync(ids, ct);
        public Task<Result> ReplaceAsync(ItemId id, SubscriptionReplacement replacement, CancellationToken ct) => inner.ReplaceAsync(id, replacement, ct);
        public Task<Result> EditAsync(ItemId id, SubscriptionEdit edit, CancellationToken ct) => inner.EditAsync(id, edit, ct);
        public Task<Result<BillingPhaseId>> AddPhaseAsync(ItemId id, BillingPhase phase, RowOrigin origin, CancellationToken ct) => inner.AddPhaseAsync(id, phase, origin, ct);
        public Task<Result> ClosePhaseAsync(ItemId id, BillingPhaseId phase, NodaTime.LocalDate endsOn, int? maxCycles, CancellationToken ct) => inner.ClosePhaseAsync(id, phase, endsOn, maxCycles, ct);
        public Task<Result<PurchaseId>> AddPurchaseAsync(Purchase purchase, RowOrigin origin, CancellationToken ct) => inner.AddPurchaseAsync(purchase, origin, ct);
        public Task<Result> ReplacePhaseAsync(ItemId id, BillingPhase phase, RowOrigin origin, CancellationToken ct) => inner.ReplacePhaseAsync(id, phase, origin, ct);
        public Task<Result> ReplacePurchaseAsync(Purchase purchase, RowOrigin origin, CancellationToken ct) => inner.ReplacePurchaseAsync(purchase, origin, ct);
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedRow>>> FindImportedAsync(ImportedTable table, IReadOnlyCollection<string> refs, CancellationToken ct) => inner.FindImportedAsync(table, refs, ct);
        public Task<StoredRows> ReadAllAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct) => inner.ReadAllAsync(ids, ct);
        public Task<IReadOnlyList<ItemId>> GrantedItemsAsync(PurchaseId purchase, CancellationToken ct) => inner.GrantedItemsAsync(purchase, ct);
        public Task<Result> DeletePurchaseAsync(ItemId id, PurchaseId purchase, CancellationToken ct) => inner.DeletePurchaseAsync(id, purchase, ct);
        public Task<IReadOnlyDictionary<ItemId, string>> ProviderKeysAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct) => inner.ProviderKeysAsync(ids, ct);
    }
}
