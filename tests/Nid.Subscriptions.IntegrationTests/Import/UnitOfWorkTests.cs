using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Subscriptions.Contracts;
using Nid.TestSupport;
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

    [Fact]
    public async Task A_second_scope_inside_the_first_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Nid.Platform.Contracts.IUnitOfWork>();

        await using var outer = await unitOfWork.BeginAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.BeginAsync(default));
    }

    /// <summary>Delegates everything, and refuses creation after the Core item already exists.</summary>
    private sealed class FailingCreateStore(ISubscriptionStore inner) : ISubscriptionStore
    {
        public Task<Result> CreateAsync(ItemId id, NewSubscription subscription, CancellationToken ct)
            => Task.FromResult(Result.Failure(new Failure("test.create_refused", "Refused on purpose.", null)));

        public Task<SubscriptionInput?> LoadAsync(ItemId id, CancellationToken ct) => inner.LoadAsync(id, ct);
        public Task<IReadOnlyList<SubscriptionInput>> LoadManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct) => inner.LoadManyAsync(ids, ct);
        public Task<Result> EditAsync(ItemId id, SubscriptionEdit edit, CancellationToken ct) => inner.EditAsync(id, edit, ct);
        public Task<Result<BillingPhaseId>> AddPhaseAsync(ItemId id, BillingPhase phase, CancellationToken ct) => inner.AddPhaseAsync(id, phase, ct);
        public Task<Result> ClosePhaseAsync(ItemId id, BillingPhaseId phase, NodaTime.LocalDate endsOn, CancellationToken ct) => inner.ClosePhaseAsync(id, phase, endsOn, ct);
        public Task<Result<PurchaseId>> AddPurchaseAsync(Purchase purchase, CancellationToken ct) => inner.AddPurchaseAsync(purchase, ct);
        public Task<IReadOnlyList<ItemId>> GrantedItemsAsync(PurchaseId purchase, CancellationToken ct) => inner.GrantedItemsAsync(purchase, ct);
        public Task<Result> DeletePurchaseAsync(ItemId id, PurchaseId purchase, CancellationToken ct) => inner.DeletePurchaseAsync(id, purchase, ct);
    }
}
