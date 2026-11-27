using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Npgsql;

namespace Nid.Banking.IntegrationTests;

/// <summary>A stored card on no account goes with its connection's only checking account, and its lines go through the next pass again.</summary>
public class AttachLoneCardsMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Before = "20261125124015_PatternEmbeddings450";

    [Fact]
    public async Task A_lone_card_goes_with_its_connections_only_checking_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await BankingApi.StartAsync(fixture);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a",
            BankingApi.Account("uid-1", "hash-1", null, "Courant"), BankingApi.Account("uid-2", "hash-2", null, "Carte", "CARD"));
        await BankingApi.ConnectAsync(app, app.AdultB, "code-b", BankingApi.Account("uid-3", "hash-3", null, "Courant 1"),
            BankingApi.Account("uid-4", "hash-4", null, "Courant 2"), BankingApi.Account("uid-5", "hash-5", null, "Carte B", "CARD"));
        var a = await ByNameAsync(app, app.AdultA);
        var card = a["Carte"].GetProperty("id").GetGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(card,
                [new("c1", new LocalDate(2026, 10, 12), null, -42m, Currency.Eur, "CARTE SHOP", null, null, "api")], ct, reconciled: true);
        }
        // Stored before connecting attached it.
        await ExecuteAsync(app.ConnectionString, $"update banking.account set card_of_account_id = null where id = '{card}'", ct);

        await using (var banking = PostgresFixture.NewBankingContext(app.ConnectionString))
        {
            await banking.GetService<IMigrator>().MigrateAsync(Before, ct);
            await banking.GetService<IMigrator>().MigrateAsync(null, ct);
        }

        Assert.Equal(a["Courant"].GetProperty("id").GetGuid(), (await ByNameAsync(app, app.AdultA))["Carte"].GetProperty("cardOfAccountId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await ByNameAsync(app, app.AdultB))["Carte B"].GetProperty("cardOfAccountId").ValueKind);
        Assert.Equal(1L, await ScalarAsync(app.ConnectionString, $"select count(*) from banking.transaction where account_id = '{card}' and reconciled_at is null", ct));
    }

    private static async Task<Dictionary<string, JsonElement>> ByNameAsync(NidApp app, PersonId actor)
        => (await BankingApi.AccountsAsync(app, actor)).EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
