using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Banking.IntegrationTests;

/// <summary>#353: what tells a card apart — the bank's holder and product, its latest lines, and the checking account it likely belongs to.</summary>
public class AccountOverviewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static NewBankTransaction Line(string reference, LocalDate on, decimal amount, string label = "CB BOULANGERIE")
        => new(reference, on, null, amount, Currency.Eur, label, null, null, "api");

    private static async Task AddAsync(NidApp app, Guid account, params NewBankTransaction[] lines)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(account, lines, default);
    }

    private static async Task<JsonElement> OverviewAsync(NidApp app, PersonId person, Guid account)
        => JsonDocument.Parse(await app.GetRawAsync(person, $"{BankingApi.Root}/accounts/{account}/overview")).RootElement;

    [Fact]
    public async Task A_card_shows_its_holder_product_latest_lines_and_its_likely_account()
    {
        await using var app = await BankingApi.StartAsync(fixture, overrides: s => s.AddSingleton<IClock>(new FixedClock(Instant.FromUtc(2026, 10, 10, 10, 0))));
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant 1"), BankingApi.Account("uid-2", "hash-2", null, "Courant 2"),
            BankingApi.Account("uid-3", "hash-3", null, "", "CARD", debit: CardDebit.Immediate, numberLast4: "4321", holder: "MME ALICE MARTIN", product: "Visa Classic"));
        var accounts = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().ToList();
        Guid Id(string name) => accounts.Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
        var card = accounts.Single(a => a.GetProperty("kind").GetString() == "card");
        Assert.Equal(("4321", "MME ALICE MARTIN", "Visa Classic"),
            (card.GetProperty("ibanMasked").GetString(), card.GetProperty("holder").GetString(), card.GetProperty("product").GetString()));
        var cardId = card.GetProperty("id").GetGuid();
        await AddAsync(app, cardId, Line("c1", D(10, 1), -4.20m), Line("c2", D(10, 3), -60m, "CB ESSENCE"), Line("c3", D(10, 5), -8m), Line("c4", D(10, 7), -12m));
        // « Courant 2 » echoes the card's debits: it is the one the card pays from.
        await AddAsync(app, Id("Courant 2"), Line("k1", D(10, 3), -60m, "CB ESSENCE"), Line("k2", D(10, 8), -12m));
        await AddAsync(app, Id("Courant 1"), Line("k3", D(10, 3), -900m, "LOYER"));

        var overview = await OverviewAsync(app, app.AdultA, cardId);

        Assert.Equal(["CB BOULANGERIE", "CB BOULANGERIE", "CB ESSENCE"], overview.GetProperty("recent").EnumerateArray().Select(l => l.GetProperty("label").GetString()));
        Assert.Equal("-12.00", overview.GetProperty("recent")[0].GetProperty("amount").GetString());
        Assert.Equal(Id("Courant 2"), overview.GetProperty("suggestedCardOf").GetGuid());

        // Attached, it suggests nothing more; another adult does not read it.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{cardId}", new { cardOfAccountId = Id("Courant 1") })).StatusCode);
        Assert.False((await OverviewAsync(app, app.AdultA, cardId)).TryGetProperty("suggestedCardOf", out var none) && none.ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, $"{BankingApi.Root}/accounts/{cardId}/overview")).StatusCode);
    }
}
