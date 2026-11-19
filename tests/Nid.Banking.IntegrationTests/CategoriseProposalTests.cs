using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.ProposalKindHelpers;

namespace Nid.Banking.IntegrationTests;

/// <summary>Transaction categories spec §Hints and the assistant: `banking.categorise_transactions`, checked as submitted, applied as approved.</summary>
public class CategoriseProposalTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Kind = "banking.categorise_transactions";
    private static readonly Instant At = new LocalDate(2026, 10, 29).At(new LocalTime(10, 0)).InUtc().ToInstant();

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => At;
    }

    private static Guid Seed(string key) => CategorySeed.Find(key)!.Id;

    private sealed record Setup(NidApp App, Guid Courant, Guid Joint, Guid Livret, Guid OfB) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// A's « Courant A » and « Joint A » each on an envelope of A's that B reads but does not write, A's « Livret A », which B cannot read,
    /// and B's own « Courant B ».
    /// </summary>
    private static async Task<Setup> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock()));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant A"),
            BankingApi.Account("uid-2", "hash-2", null, "Joint A"), BankingApi.Account("uid-3", "hash-3", null, "Livret A", "SVGS"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant B"));
        var ofA = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("id").GetGuid());
        foreach (var name in new[] { "Courant A", "Joint A" })
            Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{ofA[name]}",
                new { envelopeId = (await app.NewEnvelopeAsync(name, app.AdultA, Visibility.Shared)).Value })).StatusCode);
        var ofB = (await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Courant B");
        return new Setup(app, ofA["Courant A"], ofA["Joint A"], ofA["Livret A"], ofB.GetProperty("id").GetGuid());
    }

    private static async Task<Guid> AddAsync(NidApp app, Guid account, string reference, decimal amount, string label = "CARREFOUR CITY")
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [new NewBankTransaction(reference, new LocalDate(2026, 10, 5), null, amount, Currency.Eur, label, null, null, "api")], default);
        return (await store.ListAsync([account], null, null, 0, 100, default)).Single(t => t.ProviderRef == reference).Id;
    }

    private static async Task<BankTransaction> LineAsync(NidApp app, Guid line)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await scope.ServiceProvider.GetRequiredService<ITransactionStore>().FindAsync(line, default))!;
    }

    private static Task<HttpResponseMessage> ProposeAsync(NidApp app, PersonId person, params (Guid Line, Guid Category)[] lines)
        => app.RawAsync(person, HttpMethod.Post, Proposals, new
        {
            kind = Kind, payload = new { lines = lines.Select(l => new { transactionId = l.Line, categoryId = l.Category }).ToArray() },
        });

    private static async Task<JsonElement> PreviewAsync(NidApp app, PersonId person, Guid proposal)
        => (await JsonOf(await app.RawAsync(person, HttpMethod.Get, $"{Proposals}/{proposal}"))).GetProperty("preview");

    [Fact]
    public async Task Approval_applies_and_skips_unwritable_and_hand_lines()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var fresh = await AddAsync(app, s.OfB, "b1", -30m);
        var hinted = await AddAsync(app, s.OfB, "b2", -40m, "CARREFOUR MARKET");
        var later = await AddAsync(app, s.OfB, "b3", -25m, "DECATHLON");
        var readOnly = await AddAsync(app, s.Courant, "a1", -12m);
        var lost = await AddAsync(app, s.Joint, "j1", -8m);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ICategoryStore>()
                .SetForLinesAsync([hinted], Seed("misc"), CategorySource.Hint, "job", At, false, TestContext.Current.CancellationToken);
        }
        var groceries = Seed("food.groceries");

        var proposed = await ProposeAsync(app, app.AdultB, (fresh, groceries), (hinted, groceries), (later, Seed("leisure.sport")),
            (readOnly, groceries), (lost, groceries));
        Assert.True(proposed.StatusCode == HttpStatusCode.Created, await proposed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var proposal = await JsonOf(proposed);
        var id = proposal.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("targetItemId").ValueKind);
        // Nothing changes until approved.
        Assert.Null((await LineAsync(app, fresh)).Category);

        var preview = await PreviewAsync(app, app.AdultB, id);
        Assert.Equal((5, 0), (preview.GetProperty("count").GetInt32(), preview.GetProperty("hidden").GetInt32()));
        var shown = preview.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("transactionId").GetGuid());
        Assert.Equal(("CARREFOUR MARKET", "Courant B", "-40.00", "Divers", "Courses", true), (shown[hinted].GetProperty("label").GetString(),
            shown[hinted].GetProperty("accountName").GetString(), shown[hinted].GetProperty("amount").GetString(),
            shown[hinted].GetProperty("current").GetString(), shown[hinted].GetProperty("proposed").GetString(), shown[hinted].GetProperty("applies").GetBoolean()));
        Assert.False(shown[readOnly].GetProperty("applies").GetBoolean());
        // A never sees B's proposal: without a target, its submitter alone reads it.
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Proposals}/{id}")).StatusCode);

        // Meanwhile B chooses one line by hand, and A takes « Joint A » off the envelope: B no longer reads it.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"{BankingApi.Root}/transactions/{later}/category",
            new { categoryId = Seed("misc") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{s.Joint}",
            new { clearEnvelope = true })).StatusCode);
        preview = await PreviewAsync(app, app.AdultB, id);
        Assert.Equal((5, 1, 4), (preview.GetProperty("count").GetInt32(), preview.GetProperty("hidden").GetInt32(), preview.GetProperty("lines").GetArrayLength()));
        Assert.DoesNotContain(lost, preview.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("transactionId").GetGuid()));

        var approved = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Proposals}/{id}/approve", new { version = proposal.GetProperty("version").GetUInt32() });

        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { fresh, hinted }.Order(), (await JsonOf(approved)).GetProperty("appliedRefs").EnumerateArray().Select(r => r.GetGuid()).Order());
        foreach (var line in new[] { fresh, hinted })
        {
            var written = await LineAsync(app, line);
            Assert.Equal((groceries, CategorySource.Assistant), (written.Category, written.CategorySource));
        }
        var kept = await LineAsync(app, later);
        Assert.Equal((Seed("misc"), CategorySource.Hand), (kept.Category, kept.CategorySource));
        Assert.Null((await LineAsync(app, readOnly)).CategorySource);
        Assert.Null((await LineAsync(app, lost)).CategorySource);
    }

    [Fact]
    public async Task Unknown_or_archived_category_is_refused_at_submit()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.OfB, "b1", -30m);
        var secret = await AddAsync(app, s.Livret, "l1", -30m);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.delivery")}",
            new { archived = true })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await ProposeAsync(app, app.AdultB, (line, Guid.NewGuid()))).StatusCode);
        await BankingApi.AssertRefusedAsync(await ProposeAsync(app, app.AdultB, (line, Seed("food.delivery"))),
            HttpStatusCode.UnprocessableEntity, BankingCodes.CategoryArchived);
        // A line B cannot read answers as an unknown one: its existence is not told.
        var unread = await ProposeAsync(app, app.AdultB, (secret, Seed("food.groceries")));
        var unknown = await ProposeAsync(app, app.AdultB, (Guid.NewGuid(), Seed("food.groceries")));
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (unread.StatusCode, unknown.StatusCode));
        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Proposals}?status=pending"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_category_archived_since_submission_is_skipped_at_approval()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var kept = await AddAsync(app, s.OfB, "b1", -30m);
        var dropped = await AddAsync(app, s.OfB, "b2", -18m, "UBER EATS");
        var proposal = await JsonOf(await ProposeAsync(app, app.AdultB, (kept, Seed("food.groceries")), (dropped, Seed("food.delivery"))));
        var id = proposal.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/categories/{Seed("food.delivery")}",
            new { archived = true })).StatusCode);

        var shown = (await PreviewAsync(app, app.AdultB, id)).GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("transactionId").GetGuid());
        Assert.Equal((true, false), (shown[kept].GetProperty("applies").GetBoolean(), shown[dropped].GetProperty("applies").GetBoolean()));
        var approved = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Proposals}/{id}/approve", new { version = proposal.GetProperty("version").GetUInt32() });

        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal([kept], (await JsonOf(approved)).GetProperty("appliedRefs").EnumerateArray().Select(r => r.GetGuid()));
        Assert.Equal((Seed("food.groceries"), CategorySource.Assistant), ((await LineAsync(app, kept)).Category, (await LineAsync(app, kept)).CategorySource));
        Assert.Null((await LineAsync(app, dropped)).CategorySource);
    }

    [Fact]
    public async Task More_than_100_lines_is_refused()
    {
        await using var s = await StartAsync(fixture);
        var app = s.App;
        var line = await AddAsync(app, s.OfB, "b1", -30m);
        var groceries = Seed("food.groceries");

        await BankingApi.AssertRefusedAsync(await ProposeAsync(app, app.AdultB, [.. Enumerable.Range(0, 101).Select(_ => (Guid.NewGuid(), groceries))]),
            HttpStatusCode.UnprocessableEntity, BankingCodes.ProposalLines);
        await BankingApi.AssertRefusedAsync(await ProposeAsync(app, app.AdultB, (line, groceries), (line, Seed("misc"))),
            HttpStatusCode.UnprocessableEntity, BankingCodes.ProposalLines);
        await BankingApi.AssertRefusedAsync(await ProposeAsync(app, app.AdultB), HttpStatusCode.UnprocessableEntity, BankingCodes.ProposalLines);

        // Approved as proposed only.
        var proposal = await JsonOf(await ProposeAsync(app, app.AdultB, (line, groceries)));
        var edited = await app.RawAsync(app.AdultB, HttpMethod.Post, $"{Proposals}/{proposal.GetProperty("id").GetGuid()}/approve",
            new { version = proposal.GetProperty("version").GetUInt32(), editedPayload = new { lines = new[] { new { transactionId = line, categoryId = Seed("misc") } } } });
        await BankingApi.AssertRefusedAsync(edited, HttpStatusCode.UnprocessableEntity, BankingCodes.ProposalNotEditable);
        Assert.Null((await LineAsync(app, line)).CategorySource);
    }
}
