using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>#16: <see cref="SavingsFlows"/>, the bank lines of an account associated with a savings envelope kept as the envelope's flows.</summary>
public class SavingsFlowsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateRange Everything = DateRange.Create(LocalDate.MinIsoValue, null).TryGetValue(out var r) ? r : throw new InvalidOperationException();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(NidApp App, Guid Account, ItemId Envelope, LocalDate Day) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>Alice's « Livret Alice » account associated with her envelope of <paramref name="kind"/>; <c>Day</c> is the first day it counts from.</summary>
    private async Task<Setup> AssociatedAsync(string kind = "savings", Visibility visibility = Visibility.Shared)
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Livret Alice"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Livret Alice", app.AdultA, visibility, kind: kind);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var associatedOn = (await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindAsync(account, Ct))!.AssociatedOn!.Value;
        return new Setup(app, account, envelope, associatedOn.With(DateAdjusters.StartOfMonth));
    }

    private static async Task<Guid> LineAsync(Setup s, LocalDate on, decimal amount, string label)
    {
        await using var scope = s.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var reference = $"ref-{Guid.NewGuid():N}";
        await store.AddAsync(s.Account, [new NewBankTransaction(reference, on, null, amount, Currency.Eur, label, null, null, "api")], Ct);
        return (await store.ListAsync([s.Account], on, on, 0, 100, Ct)).Single(l => l.ProviderRef == reference).Id;
    }

    private static async Task<Guid> InterestCategoryAsync(Setup s)
    {
        await using var scope = s.App.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICategoryStore>().ResolveKeyAsync("income.interest", Ct))!.Value;
    }

    private static async Task AsInterestAsync(Setup s, Guid line)
    {
        await using var scope = s.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
            .SetForLinesAsync([line], await InterestCategoryAsync(s), CategorySource.Hand, "test", BankingApi.Now, byPerson: true, Ct);
    }

    private static async Task<SavingsFlowCounts> RunAsync(Setup s, PersonId? reader, Guid? line = null)
    {
        await using var scope = s.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var flows = scope.ServiceProvider.GetRequiredService<SavingsFlows>();
        return line is { } id ? await flows.RunForLineAsync(id, reader, Ct) : await flows.RunAsync(null, reader, Ct);
    }

    private static async Task<IReadOnlyList<Flow>> FlowsAsync(Setup s)
    {
        await using var scope = s.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(s.App.AdultA);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IFlows>().ListAsync(s.Envelope, Everything, null, Ct)).TryGetValue(out var flows));
        return flows!;
    }

    [Fact]
    public async Task Deposits_and_interest_of_a_savings_account_become_flows()
    {
        await using var s = await AssociatedAsync();
        var deposit = await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        var interest = await LineAsync(s, s.Day.PlusDays(2), 5m, "INTERETS 2026");
        var withdrawal = await LineAsync(s, s.Day.PlusDays(3), -40m, "VIR COURANT");
        var split = await LineAsync(s, s.Day.PlusDays(4), 60m, "VIR ET INTERETS");
        await AsInterestAsync(s, interest);
        await using (var scope = s.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ISplitStore>().SplitAsync(split,
                [new PartInput(50m, null, null), new PartInput(10m, await InterestCategoryAsync(s), null)], "test", BankingApi.Now, Ct);

        var counts = await RunAsync(s, s.App.AdultA);

        Assert.Equal(new SavingsFlowCounts(5, 0, 0, 0), counts);
        var flows = (await FlowsAsync(s)).ToDictionary(f => f.ExternalRef!);
        Assert.Equal(
            new (string, FlowKind, decimal, LocalDate, string?, string?)[]
            {
                (SavingsFlowPlan.MovementRef(deposit), FlowKind.Contribution, 100m, s.Day, null, "VIR LIVRET"),
                (SavingsFlowPlan.InterestRef(interest), FlowKind.Income, 5m, s.Day.PlusDays(2), "interest", "INTERETS 2026"),
                (SavingsFlowPlan.MovementRef(withdrawal), FlowKind.Withdrawal, 40m, s.Day.PlusDays(3), null, "VIR COURANT"),
                (SavingsFlowPlan.MovementRef(split), FlowKind.Contribution, 50m, s.Day.PlusDays(4), null, "VIR ET INTERETS"),
                (SavingsFlowPlan.InterestRef(split), FlowKind.Income, 10m, s.Day.PlusDays(4), "interest", "VIR ET INTERETS"),
            }.OrderBy(x => x.Item1),
            flows.Values.Select(f => (f.ExternalRef!, f.Kind, f.Amount.Amount, f.OccurredOn, f.Category, f.Note)).OrderBy(x => x.Item1));
    }

    [Fact]
    public async Task A_second_run_writes_nothing()
    {
        await using var s = await AssociatedAsync();
        await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        await AsInterestAsync(s, await LineAsync(s, s.Day.PlusDays(2), 5m, "INTERETS 2026"));
        Assert.Equal(new SavingsFlowCounts(2, 0, 0, 0), await RunAsync(s, s.App.AdultA));
        var before = await FlowsAsync(s);

        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 0), await RunAsync(s, s.App.AdultA));
        Assert.Equal(before.Select(f => f.Id).Order(), (await FlowsAsync(s)).Select(f => f.Id).Order());
    }

    [Fact]
    public async Task Recategorising_a_line_as_interest_turns_its_contribution_into_income()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 5m, "INTERETS 2026");
        await RunAsync(s, s.App.AdultA);
        Assert.Equal([SavingsFlowPlan.MovementRef(line)], (await FlowsAsync(s)).Select(f => f.ExternalRef));

        await AsInterestAsync(s, line);
        await RunAsync(s, s.App.AdultA, line);

        var flow = Assert.Single(await FlowsAsync(s));
        Assert.Equal((SavingsFlowPlan.InterestRef(line), FlowKind.Income, 5m), (flow.ExternalRef, flow.Kind, flow.Amount.Amount));
    }

    [Fact]
    public async Task A_typed_versement_absorbs_its_line_and_stays_untouched()
    {
        await using var s = await AssociatedAsync();
        Flow typed;
        await using (var scope = s.App.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(s.App.AdultA);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IFlows>().RecordAsync(
                new NewFlow(s.Envelope, s.Day, FlowKind.Contribution, Money.Create(100m, Currency.Eur).TryGetValue(out var amount) ? amount! : throw new InvalidOperationException(),
                    null, null, "Versement"), Ct)).TryGetValue(out typed!));
        }
        await LineAsync(s, s.Day.PlusDays(1), 100m, "VIR LIVRET");

        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 0), await RunAsync(s, s.App.AdultA));

        Assert.Equal(typed, Assert.Single(await FlowsAsync(s)));
    }

    [Fact]
    public async Task A_flow_whose_line_was_purged_survives()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        await RunAsync(s, s.App.AdultA);
        await using (var scope = s.App.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ITransactionStore>().PurgeBookedBeforeAsync(s.Day.PlusDays(1), [], Ct));

        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 0), await RunAsync(s, s.App.AdultA));

        Assert.Equal(SavingsFlowPlan.MovementRef(line), Assert.Single(await FlowsAsync(s)).ExternalRef);
    }

    [Fact]
    public async Task A_checking_envelope_gets_no_flows()
    {
        await using var s = await AssociatedAsync(kind: "checking");
        await LineAsync(s, s.Day, 100m, "VIR LIVRET");

        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 0), await RunAsync(s, s.App.AdultA));

        Assert.Empty(await FlowsAsync(s));
    }

    [Fact]
    public async Task An_account_nobody_may_write_is_skipped()
    {
        await using var s = await AssociatedAsync(visibility: Visibility.Private);
        await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        // Bob reads the pass; Alice, who associated it, is no longer on record: nobody left who writes Alice's private envelope.
        await using (var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>().Configure(s.App.ConnectionString).Options))
            await db.Accounts.Where(a => a.Id == s.Account).ExecuteUpdateAsync(u => u.SetProperty(a => a.AssociatedByPersonId, (Guid?)null), Ct);

        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 1), await RunAsync(s, s.App.AdultB));

        Assert.Empty(await FlowsAsync(s));
    }
}
