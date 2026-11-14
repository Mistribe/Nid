using Nid.TestSupport;
using static Nid.Childcare.IntegrationTests.ChildcareApi;

namespace Nid.Childcare.IntegrationTests;

/// <summary>June settles the reference period's paid leave from the days of its validated months, read back from the store.</summary>
public class ChildcareJuneTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task June_acquires_the_leave_of_the_validated_months_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = await CreateAsync(app, from: "2024-11-04");
        foreach (var month in new[] { "2024-11", "2024-12", "2025-01", "2025-02", "2025-03", "2025-04", "2025-05" })
            await ValidateAsync(app, url, month);

        var june = await GetAsync(app, $"{url}/statements/2025-06");
        // 4 November – 31 May: 30 weeks of care, public holidays assimilated; 30 × 2,5 ÷ 4 = 18,75, rounded up.
        Assert.Equal(19, june.GetProperty("computed").GetProperty("paidLeaveDays").GetInt32());
        Assert.Equal(19, june.GetProperty("figures").GetProperty("declaration").GetProperty("paidLeaveDays").GetInt32());
    }
}
