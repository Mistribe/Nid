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
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.IntegrationTests;

/// <summary>
/// #414 step 1: the installation application (« app-1 », the server's key) handed to one adult. Their connections made with it read on;
/// the other adult's end, their accounts kept, and they are told to declare their own application.
/// </summary>
public class InstallationTransferTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Transfer = "/api/v1/admin/banking/transfer";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BankingDbContext Db(NidApp app) => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(app.ConnectionString).Options);

    private static Task<HttpResponseMessage> TransferAsync(NidApp app, PersonId to, PersonId? actor = null)
        => app.RawAsync(actor ?? app.AdultA, HttpMethod.Post, Transfer, new { personId = to.Value });

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader);
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "banking");
        var result = await source.SignalsAsync(reader, NidApp.GoldenCaseToday, NidApp.GoldenCaseToday.PlusDays(90), Ct);
        Assert.True(result.TryGetValue(out var signals));
        return signals!;
    }

    private static async Task NightAsync(NidApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        Assert.True((await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand(), Ct)).IsSuccess);
    }

    private static async Task<JsonElement> DiagnosticsAsync(NidApp app)
        => JsonElement.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/admin/banking/diagnostics"));

    [Fact]
    public async Task The_chosen_adult_reads_on_and_the_other_adult_connection_ends()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var alice = await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-a", "hash-a", null, "Courant Alice"));
        var bob = await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant Bob"));
        await using (var db = Db(app))
        {
            // A connection from before #382, with no application id: the installation's all the same.
            var legacy = await db.Connections.SingleAsync(c => c.Id == alice, Ct);
            legacy.ApplicationId = null;
            await db.SaveChangesAsync(Ct);
        }

        var candidates = (await DiagnosticsAsync(app)).GetProperty("transferCandidates").EnumerateArray()
            .Select(c => (c.GetProperty("personId").GetGuid(), c.GetProperty("connections").GetInt32())).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(1, c.Item2));

        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(app, app.AdultA)).StatusCode);

        var mine = JsonElement.Parse(await app.GetRawAsync(app.AdultA, $"{BankingApi.Root}/application"));
        Assert.Equal(("app-1", "loaded", true), (mine.GetProperty("applicationId").GetString(), mine.GetProperty("keyStatus").GetString(), mine.GetProperty("inUse").GetBoolean()));
        var diagnostics = await DiagnosticsAsync(app);
        Assert.False(diagnostics.GetProperty("appIdSaved").GetBoolean());
        Assert.False(diagnostics.TryGetProperty("transferCandidates", out var after) && after.ValueKind != JsonValueKind.Null);
        await using (var db = Db(app))
        {
            var rows = await db.Connections.AsNoTracking().Where(c => c.Id == alice || c.Id == bob).ToDictionaryAsync(c => c.Id, Ct);
            Assert.Equal(("active", "app-1"), (rows[alice].Status, rows[alice].ApplicationId));
            Assert.Equal(("revoked", BankingCodes.ApplicationTransferred), (rows[bob].Status, rows[bob].LastErrorCode));
        }
        Assert.Equal(["session-code-b"], app.Banks.DeletedSessions);
        Assert.Equal(["app-1"], app.Banks.DeleteApps);

        app.Banks.BalanceCalls.Clear();
        await NightAsync(app);
        Assert.Equal([("app-1", "session-code-a")], app.Banks.BalanceCalls.Select(c => (c.AppId, c.SessionId)).Distinct());

        // Bob's account and its history stay, no longer synchronised.
        Assert.Contains((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray(), a => a.GetProperty("name").GetString() == "Courant Bob");
        var events = await AuditApi.EventsAsync(app, SecurityEventNames.BankingApplicationTransferred);
        Assert.Equal(app.AdultA.Value, Assert.Single(events).PersonId);
        Assert.DoesNotContain("app-1", events[0].Data);
    }

    [Fact]
    public async Task The_other_adult_is_told_until_they_link_their_bank_through_their_own_application()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var bob = await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant Bob"));
        Assert.Equal(HttpStatusCode.NoContent, (await TransferAsync(app, app.AdultA)).StatusCode);

        var signal = Assert.Single(await SignalsAsync(app, app.AdultB));
        Assert.Equal(($"banking.application_needed:{bob:D}", "banking.application_needed"), (signal.Key, signal.Kind));
        Assert.Empty(await SignalsAsync(app, app.AdultA));

        var declared = await app.RawAsync(app.AdultB, HttpMethod.Put, $"{BankingApi.Root}/application",
            new { applicationId = "app-bob", privateKey = RSA.Create(2048).ExportRSAPrivateKeyPem() });
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        Assert.Single(await SignalsAsync(app, app.AdultB)); // declared, not linked yet

        var renewed = await BankingApi.ConnectAsync(app, app.AdultB, "code-b2", BankingApi.Account("uid-b2", "hash-b", null, "Courant Bob"));
        Assert.NotEqual(bob, renewed);
        Assert.Empty(await SignalsAsync(app, app.AdultB));
        Assert.Equal("app-bob", app.Banks.StartAuths[^1].AppId);
        // The same account, found again by its identification hash.
        Assert.Single((await BankingApi.AccountsAsync(app, app.AdultB)).EnumerateArray(), a => a.GetProperty("name").GetString() == "Courant Bob");
    }

    [Fact]
    public async Task An_adult_whose_own_application_is_in_use_is_refused()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultB, HttpMethod.Put, $"{BankingApi.Root}/application",
            new { applicationId = "app-bob", privateKey = RSA.Create(2048).ExportRSAPrivateKeyPem() })).StatusCode);
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-b", "hash-b", null, "Courant Bob"));

        await BankingApi.AssertRefusedAsync(await TransferAsync(app, app.AdultB), HttpStatusCode.Conflict, BankingCodes.ApplicationInUse);

        Assert.True((await DiagnosticsAsync(app)).GetProperty("appIdSaved").GetBoolean());
    }

    [Fact]
    public async Task Only_an_administrator_transfers()
    {
        await using var app = await BankingApi.StartAsync(fixture);

        Assert.Equal(HttpStatusCode.Forbidden, (await TransferAsync(app, app.AdultB, app.AdultB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TransferAsync(app, app.Dependent)).StatusCode);
    }
}
