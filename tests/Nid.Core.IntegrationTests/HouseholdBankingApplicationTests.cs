using System.Net;
using System.Text.Json;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests;

/// <summary>
/// #414: the installation-wide Enable Banking application is deprecated. The admin route never sets a new one; an installation that has one
/// keeps it (sent again unchanged) until it clears it, and cannot set it back.
/// </summary>
[Collection("postgres")]
public class HouseholdBankingApplicationTests(PostgresFixture postgres)
{
    private const string Household = "/api/v1/admin/household";

    private static async Task AssertDeprecatedAsync(HttpResponseMessage response)
    {
        await BankingApi.AssertRefusedAsync(response, HttpStatusCode.UnprocessableEntity, BankingApplicationCodes.InstallationDeprecated);
        var errors = (await BankingApi.JsonAsync(response)).GetProperty("errors").EnumerateArray().Select(e => e.FieldPath()).ToList();
        Assert.Contains("enableBankingAppId", errors);
    }

    [Fact]
    public async Task A_household_without_one_cannot_set_it()
    {
        await using var app = await BankingApi.StartAsync(postgres, enable: false);

        await AssertDeprecatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { bankingEnabled = true, enableBankingAppId = "app-1" }));

        var settings = JsonElement.Parse(await app.GetRawAsync(app.AdultA, Household));
        Assert.Equal((false, JsonValueKind.Null), (settings.GetProperty("bankingEnabled").GetBoolean(), settings.GetProperty("enableBankingAppId").ValueKind));
        // Clearing what is not there, or saying nothing of it, is no change.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { bankingEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task One_already_saved_is_kept_or_cleared_never_replaced_nor_set_back()
    {
        await using var app = await BankingApi.StartAsync(postgres); // saved before #414: app-1

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = " app-1 " })).StatusCode);
        await AssertDeprecatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "app-new" }));
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "" })).StatusCode);
        await AssertDeprecatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Patch, Household, new { enableBankingAppId = "app-1" }));
    }

    [Fact]
    public void The_shared_code_is_the_banking_one() => Assert.Equal(BankingApplicationCodes.InUse, BankingCodes.ApplicationInUse);
}
