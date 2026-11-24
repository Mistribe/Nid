using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.TestSupport;
using UnambitiousFx.Functional;

namespace Nid.Banking.IntegrationTests;

/// <summary>#513: Banking's own settings, moved from <c>core.household_settings</c> with the same rules.</summary>
public class BankingSettingsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string? Code(Result<BankingSettingsView> result) => result.TryGetFailure(out var failure) ? failure!.Code : null;

    private static BankingSettingsView Saved(Result<BankingSettingsView> result)
    {
        Assert.True(result.TryGetValue(out var view));
        return view!;
    }

    [Fact]
    public async Task The_application_id_is_trimmed_kept_when_left_out_and_cleared_when_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = PostgresFixture.NewBankingContext(await fixture.NewConnectionStringAsync());
        var settings = new BankingSettingsStore(db);
        Assert.Equal(new BankingSettingsView(null, null), await settings.ReadAsync(ct));

        Assert.Equal("0a1b-app", Saved(await settings.UpdateAsync(new BankingSettingsChange(EnableBankingAppId: " 0a1b-app "), ct)).EnableBankingAppId);
        Assert.Equal("0a1b-app", Saved(await settings.UpdateAsync(new BankingSettingsChange(BankRetentionMonths: 12), ct)).EnableBankingAppId);
        Assert.Null(Saved(await settings.UpdateAsync(new BankingSettingsChange(EnableBankingAppId: ""), ct)).EnableBankingAppId);
    }

    [Fact]
    public async Task Bank_retention_is_unlimited_by_default_between_1_and_600_months_and_can_be_cleared()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = PostgresFixture.NewBankingContext(await fixture.NewConnectionStringAsync());
        var settings = new BankingSettingsStore(db);
        Assert.Null((await settings.ReadAsync(ct)).BankRetentionMonths);

        Assert.Equal(12, Saved(await settings.UpdateAsync(new BankingSettingsChange(BankRetentionMonths: 12), ct)).BankRetentionMonths);
        // A field left out is left unchanged.
        Assert.Equal(12, Saved(await settings.UpdateAsync(new BankingSettingsChange(), ct)).BankRetentionMonths);
        Assert.Equal(BankingCodes.InvalidBankRetention, Code(await settings.UpdateAsync(new BankingSettingsChange(BankRetentionMonths: 0), ct)));
        Assert.Equal(BankingCodes.InvalidBankRetention, Code(await settings.UpdateAsync(new BankingSettingsChange(BankRetentionMonths: 601), ct)));
        Assert.Equal(600, Saved(await settings.UpdateAsync(new BankingSettingsChange(BankRetentionMonths: 600), ct)).BankRetentionMonths);
        Assert.Null(Saved(await settings.UpdateAsync(new BankingSettingsChange(ClearBankRetention: true), ct)).BankRetentionMonths);
    }
}
