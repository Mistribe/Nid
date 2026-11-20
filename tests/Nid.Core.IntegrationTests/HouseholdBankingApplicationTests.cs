using System.Net;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests;

/// <summary>#382: the installation's Enable Banking application id cannot change while connections use it.</summary>
[Collection("postgres")]
public class HouseholdBankingApplicationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_installation_id_is_kept_while_a_connection_uses_it()
    {
        await using var app = await BankingApi.StartAsync(postgres);
        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        const string Household = "/api/v1/admin/household";

        await BankingApi.AssertRefusedAsync(
            await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "app-new" }), HttpStatusCode.Conflict, BankingCodes.ApplicationInUse);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "app-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "" })).StatusCode);
    }

    [Fact]
    public void The_shared_code_is_the_banking_one() => Assert.Equal(BankingApplicationCodes.InUse, BankingCodes.ApplicationInUse);
}
