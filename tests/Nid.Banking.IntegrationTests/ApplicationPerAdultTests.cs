using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Sync;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.IntegrationTests;

/// <summary>#382: every Enable Banking call for a connection uses the application the connection was made with, its creator's own (#414).</summary>
public class ApplicationPerAdultTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Iban = "FR76 3000 6000 0112 3456 7890 189";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    /// <summary>Both adults on « app-home », their own; a test replaces Alice's with <see cref="SaveAsync"/>.</summary>
    private static async Task<NidApp> StartAsync(PostgresFixture fixture)
    {
        var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ApplicationAsync(app, app.AdultA, "app-home");
        await BankingApi.ApplicationAsync(app, app.AdultB, "app-home");
        return app;
    }

    private static async Task DeleteAsync(NidApp app, PersonId person)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True(await scope.ServiceProvider.GetRequiredService<IApplicationStore>().DeleteAsync(person, Ct));
    }

    private static async Task SaveAsync(NidApp app, PersonId person, string id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IApplicationStore>().SaveAsync(person, id, RSA.Create(2048).ExportRSAPrivateKeyPem(), Ct);
    }

    private static async Task<HttpResponseMessage> CallbackOkAsync(NidApp app, PersonId person, string state, string code)
    {
        var callback = await BankingApi.CallbackAsync(app, person, $"state={state}&code={code}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return callback;
    }

    private static async Task NightAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand());
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Alice_with_her_own_application_connects_through_it()
    {
        await using var app = await StartAsync(fixture);
        await SaveAsync(app, app.AdultA, "app-alice");
        app.Banks.Sessions["code-a"] = new AuthorizedSession("session-a", BankingApi.ValidUntil, [BankingApi.Account("uid-1", "hash-1", null, "Courant")]);

        var (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        await CallbackOkAsync(app, app.AdultA, state, "code-a");

        Assert.Equal("app-alice", app.Banks.StartAuths[0].AppId);
        Assert.Equal(["app-alice"], app.Banks.SessionApps);
    }

    private static async Task SetRowAsync(NidApp app, Guid id, string applicationId, string? status = null)
    {
        await using var db = Db(app);
        var row = await db.Connections.SingleAsync(c => c.Id == id, Ct);
        row.ApplicationId = applicationId;
        if (status is not null)
            row.Status = status;
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_connection_whose_application_is_gone_is_still_deleted()
    {
        await using var app = await StartAsync(fixture);
        var expired = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        var active = await BankingApi.ConnectAsync(app, app.AdultA, "code-b", BankingApi.Account("uid-2", "hash-2", null, "Epargne"));
        await SetRowAsync(app, expired, "app-vanished", "expired");
        await SetRowAsync(app, active, "app-vanished");

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{expired}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{active}")).StatusCode);

        Assert.Empty(app.Banks.DeleteApps); // nothing could be revoked at the bank
        Assert.Empty(await app.GetAsync<List<JsonElement>>(app.AdultA, $"{BankingApi.Root}/connections"));
    }

    [Fact]
    public async Task Bob_without_one_cannot_link_a_bank()
    {
        await using var app = await StartAsync(fixture);
        await DeleteAsync(app, app.AdultB);

        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultB, HttpMethod.Post, $"{BankingApi.Root}/connections", new { aspspName = "Banque Alpha" }),
            HttpStatusCode.Conflict, BankingCodes.NotConfigured);
        Assert.Empty(app.Banks.StartAuths);
    }

    [Fact]
    public async Task A_connection_made_with_an_application_she_replaced_is_no_longer_renewed()
    {
        await using var app = await StartAsync(fixture);
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        await SaveAsync(app, app.AdultA, "app-alice");

        await BankingApi.AssertRefusedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/connections/{id}/renew"),
            HttpStatusCode.Conflict, BankingCodes.NotConfigured);
        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{BankingApi.Root}/connections/{id}")).StatusCode);
        Assert.Empty(app.Banks.DeleteApps); // no key left to revoke its session with: it lapses at the bank
    }

    [Fact]
    public async Task Linking_the_bank_again_revokes_the_old_session_with_its_own_application()
    {
        await using var app = await StartAsync(fixture);
        var old = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        await SaveAsync(app, app.AdultA, "app-alice");

        var renewed = await BankingApi.ConnectAsync(app, app.AdultA, "code-b", BankingApi.Account("uid-2", "hash-1", null, "Courant"));

        Assert.NotEqual(old, renewed);
        Assert.Equal(["app-home", "app-alice"], app.Banks.SessionApps);
        Assert.Empty(app.Banks.DeleteApps); // the superseded session's application is not hers any more: it lapses at the bank
    }

    [Fact]
    public async Task The_callback_uses_the_pending_connection_application()
    {
        await using var app = await StartAsync(fixture);
        app.Banks.Sessions["code-a"] = new AuthorizedSession("session-a", BankingApi.ValidUntil, [BankingApi.Account("uid-1", "hash-1", null, "Courant")]);
        var (_, state) = await BankingApi.StartConnectionAsync(app, app.AdultA);
        await SaveAsync(app, app.AdultA, "app-alice");

        // Started with « app-home », which is no longer hers: the code is not exchanged with another application.
        var callback = await CallbackOkAsync(app, app.AdultA, state, "code-a");

        Assert.Contains("error=banking.not_configured", callback.Headers.Location!.OriginalString);
        Assert.Empty(app.Banks.SessionApps);
    }

    [Fact]
    public async Task A_joint_account_on_two_applications_is_one_account_read_once()
    {
        await using var app = await StartAsync(fixture);
        await SaveAsync(app, app.AdultA, "app-alice");
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", Iban, "Compte joint"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", Iban, "Compte joint"));
        foreach (var uid in new[] { ("session-code-a", "uid-a"), ("session-code-b", "uid-b") })
            app.Banks.Balances[uid] = [new ProviderBalance("CLBD", 10m, "EUR", new LocalDate(2026, 1, 1))];

        await NightAsync(app);

        Assert.Single((await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray());
        var balance = Assert.Single(app.Banks.BalanceCalls);
        Assert.Equal(balance.SessionId == "session-code-a" ? "app-alice" : "app-home", balance.AppId);
        var transactions = Assert.Single(app.Banks.TransactionCalls);
        Assert.Equal((balance.SessionId, balance.AppId), (transactions.SessionId, transactions.AppId));
    }

    [Fact]
    public async Task A_manual_sync_whose_application_cannot_be_resolved_succeeds_and_fails_the_connection()
    {
        await using var app = await StartAsync(fixture);
        await SaveAsync(app, app.AdultA, "app-alice");
        var id = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", null, "Courant A"));
        await using var db = Db(app);
        var key = await db.Applications.AsNoTracking().Where(a => a.PersonId == app.AdultA.Value).Select(a => a.PrivateKey).SingleAsync(Ct);
        key[^1] ^= 0xFF;
        await db.Database.ExecuteSqlAsync($"update banking.application set private_key = {key} where person_id = {app.AdultA.Value}", Ct);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/sync", new { connectionId = id });

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        var row = await db.Connections.AsNoTracking().SingleAsync(c => c.Id == id, Ct);
        Assert.Equal(("error", BankingCodes.NotConfigured), (row.Status, row.LastErrorCode));
        Assert.Empty(app.Banks.BalanceCalls);
    }

    [Fact]
    public async Task An_unreadable_key_fails_only_its_connections()
    {
        await using var app = await StartAsync(fixture);
        await SaveAsync(app, app.AdultA, "app-alice");
        var alice = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", null, "Courant A"));
        var bob = await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant B"));
        app.Banks.Balances[("session-code-b", "uid-b")] = [new ProviderBalance("CLBD", 10m, "EUR", new LocalDate(2026, 1, 1))];
        await using var db = Db(app);
        var key = await db.Applications.AsNoTracking().Where(a => a.PersonId == app.AdultA.Value).Select(a => a.PrivateKey).SingleAsync(Ct);
        key[^1] ^= 0xFF;
        await db.Database.ExecuteSqlAsync($"update banking.application set private_key = {key} where person_id = {app.AdultA.Value}", Ct);

        await NightAsync(app);

        var rows = await db.Connections.AsNoTracking().ToDictionaryAsync(c => c.Id, Ct);
        Assert.Equal(("error", BankingCodes.NotConfigured), (rows[alice].Status, rows[alice].LastErrorCode));
        Assert.Null(rows[bob].LastErrorCode);
        Assert.Equal(["session-code-b"], app.Banks.BalanceCalls.Select(c => c.SessionId));
    }

    [Fact]
    public async Task Banking_is_configured_for_an_adult_with_only_their_own_application()
    {
        await using var app = await StartAsync(fixture);
        await DeleteAsync(app, app.AdultB);

        async Task<bool> ConfiguredAsync(PersonId person)
            => JsonDocument.Parse(await app.GetRawAsync(person, $"{BankingApi.Root}/status")).RootElement.GetProperty("configured").GetBoolean();

        Assert.True(await ConfiguredAsync(app.AdultA));
        Assert.False(await ConfiguredAsync(app.AdultB));
    }
}
