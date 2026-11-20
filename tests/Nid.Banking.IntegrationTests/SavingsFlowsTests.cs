using System.Globalization;
using System.Net;
using System.Text;
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
        await SplitAsync(s, split, 50m, 10m);

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

    private static async Task SplitAsync(Setup s, Guid line, decimal movement, decimal interest)
    {
        await using var scope = s.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISplitStore>().SplitAsync(line,
            [new PartInput(movement, null, null), new PartInput(interest, await InterestCategoryAsync(s), null)], "test", BankingApi.Now, Ct);
    }

    [Fact]
    public async Task Resplitting_a_line_supersedes_its_flows()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 60m, "VIR ET INTERETS");
        await SplitAsync(s, line, 50m, 10m);
        Assert.Equal(new SavingsFlowCounts(2, 0, 0, 0), await RunAsync(s, s.App.AdultA));

        await SplitAsync(s, line, 40m, 20m);

        Assert.Equal(new SavingsFlowCounts(0, 2, 0, 0), await RunAsync(s, s.App.AdultA));
        Assert.Equal(
            [(SavingsFlowPlan.MovementRef(line), FlowKind.Contribution, 40m), (SavingsFlowPlan.InterestRef(line), FlowKind.Income, 20m)],
            (await FlowsAsync(s)).Select(f => (f.ExternalRef!, f.Kind, f.Amount.Amount)).OrderBy(x => x.Item1, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_line_booked_after_today_waits_for_its_day()
    {
        await using var s = await AssociatedAsync();
        LocalDate today;
        await using (var scope = s.App.Services.CreateAsyncScope())
            today = await scope.ServiceProvider.GetRequiredService<IHouseholdCalendar>().TodayAsync(Ct);
        var booked = await LineAsync(s, today, 100m, "VIR LIVRET");
        await LineAsync(s, today.PlusDays(1), 30m, "VIR A VENIR");

        Assert.Equal(new SavingsFlowCounts(1, 0, 0, 0), await RunAsync(s, s.App.AdultA));

        Assert.Equal(SavingsFlowPlan.MovementRef(booked), Assert.Single(await FlowsAsync(s)).ExternalRef);
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

    private static async Task<LocalDate> TodayAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHouseholdCalendar>().TodayAsync(Ct);
    }

    private static async Task OkAsync(Setup s, HttpMethod method, string path, object? body = null)
    {
        var response = await s.App.RawAsync(s.App.AdultA, method, $"{BankingApi.Root}{path}", body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static IEnumerable<(string, FlowKind, decimal)> Shape(IEnumerable<Flow> flows)
        => flows.Select(f => (f.ExternalRef!, f.Kind, f.Amount.Amount)).OrderBy(x => x.Item1, StringComparer.Ordinal);

    [Fact]
    public async Task Importing_a_statement_on_a_savings_envelope_writes_its_flows()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("Livret Alice", app.AdultA, Visibility.Shared);
        var day = (await TodayAsync(app)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        static string Line(string id, string day, string amount, string name)
            => $"<STMTTRN><TRNTYPE>OTHER</TRNTYPE><DTPOSTED>{day}</DTPOSTED><TRNAMT>{amount}</TRNAMT><FITID>{id}</FITID><NAME>{name}</NAME></STMTTRN>";
        var ofx = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="211" SECURITY="NONE" OLDFILEUID="NONE" NEWFILEUID="NONE"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS>
            <CURDEF>EUR</CURDEF>
            <BANKACCTFROM><ACCTID>FR7630006000011234567890189</ACCTID><ACCTTYPE>SAVINGS</ACCTTYPE></BANKACCTFROM>
            <BANKTRANLIST><DTSTART>{day}</DTSTART><DTEND>{day}</DTEND>{Line("F1", day, "100", "VIR LIVRET")}{Line("F2", day, "-40", "VIR COURANT")}{Line("F3", day, "5", "INTERETS 2026")}</BANKTRANLIST>
            </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """);

        var response = await app.RawContentAsync(app.AdultA, HttpMethod.Post, BankingApi.Root + "/statements",
            DocumentApi.Multipart(ofx, $"fileName=releve.ofx&confirm=true&keepProbablyKnown=false&envelopeId={envelope.Value}"));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var s = new Setup(app, Guid.Empty, envelope, default);
        Assert.Equal(
            [(FlowKind.Contribution, 100m, "VIR LIVRET"), (FlowKind.Withdrawal, 40m, "VIR COURANT"), (FlowKind.Income, 5m, "INTERETS 2026")],
            (await FlowsAsync(s)).Select(f => (f.Kind, f.Amount.Amount, f.Note!)).OrderBy(x => x.Kind));
    }

    [Fact]
    public async Task Setting_a_line_category_by_hand_updates_its_flow()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 5m, "VIR 2026");
        await RunAsync(s, s.App.AdultA);

        await OkAsync(s, HttpMethod.Put, $"/transactions/{line}/category", new { categoryId = await InterestCategoryAsync(s) });

        Assert.Equal([(SavingsFlowPlan.InterestRef(line), FlowKind.Income, 5m)], Shape(await FlowsAsync(s)));

        await OkAsync(s, HttpMethod.Delete, $"/transactions/{line}/category");

        Assert.Equal([(SavingsFlowPlan.MovementRef(line), FlowKind.Contribution, 5m)], Shape(await FlowsAsync(s)));
    }

    [Fact]
    public async Task Splitting_a_line_updates_its_flows()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 60m, "VIR LIVRET");
        await RunAsync(s, s.App.AdultA);

        await OkAsync(s, HttpMethod.Put, $"/transactions/{line}/split", new
        {
            parts = new object[] { new { amount = "50" }, new { amount = "10", categoryId = await InterestCategoryAsync(s) } },
        });

        Assert.Equal(
            [(SavingsFlowPlan.MovementRef(line), FlowKind.Contribution, 50m), (SavingsFlowPlan.InterestRef(line), FlowKind.Income, 10m)],
            Shape(await FlowsAsync(s)));

        await OkAsync(s, HttpMethod.Delete, $"/transactions/{line}/split");

        Assert.Equal([(SavingsFlowPlan.MovementRef(line), FlowKind.Contribution, 60m)], Shape(await FlowsAsync(s)));
    }

    [Fact]
    public async Task Associating_an_account_writes_its_flows()
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Livret Alice"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Livret Alice", app.AdultA, Visibility.Shared);
        await using var s = new Setup(app, account, envelope, await TodayAsync(app));
        var line = await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        // Reconciled while the account was no savings one: no hint said « Intérêts » then.
        var interest = await LineAsync(s, s.Day, 5m, "INTERETS 2026");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<BankReconciliation>().RunAsync([account], s.Day, Ct);
        }

        await OkAsync(s, HttpMethod.Patch, $"/accounts/{account}", new { envelopeId = envelope.Value });

        Assert.Equal(
            [(SavingsFlowPlan.MovementRef(line), FlowKind.Contribution, 100m), (SavingsFlowPlan.InterestRef(interest), FlowKind.Income, 5m)],
            Shape(await FlowsAsync(s)));
    }

    [Fact]
    public async Task Detaching_keeps_the_flows()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        await RunAsync(s, s.App.AdultA);

        await OkAsync(s, HttpMethod.Patch, $"/accounts/{s.Account}", new { clearEnvelope = true });
        await RunAsync(s, s.App.AdultA);

        Assert.Equal([(SavingsFlowPlan.MovementRef(line), FlowKind.Contribution, 100m)], Shape(await FlowsAsync(s)));
    }

    [Fact]
    public async Task The_backfill_hints_interest_then_writes_the_flows_once()
    {
        await using var s = await AssociatedAsync();
        var line = await LineAsync(s, s.Day, 5m, "INTERETS 2026");

        async Task<SavingsFlowCounts> BackfillAsync()
        {
            await using var scope = s.App.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            return await scope.ServiceProvider.GetRequiredService<SavingsFlowBackfill>().RunAsync(Ct);
        }

        Assert.Equal(new SavingsFlowCounts(1, 0, 0, 0), await BackfillAsync());
        Assert.Equal([(SavingsFlowPlan.InterestRef(line), FlowKind.Income, 5m)], Shape(await FlowsAsync(s)));
        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 0), await BackfillAsync());
    }

    [Fact]
    public async Task The_backfill_waits_while_bank_sync_is_off()
    {
        await using var s = await AssociatedAsync();
        await LineAsync(s, s.Day, 100m, "VIR LIVRET");
        await BankingApi.SettingsAsync(s.App, new { bankingEnabled = false });

        await using var scope = s.App.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.Equal(new SavingsFlowCounts(0, 0, 0, 0), await scope.ServiceProvider.GetRequiredService<SavingsFlowBackfill>().RunAsync(Ct));

        Assert.Empty(await FlowsAsync(s));
    }
}
