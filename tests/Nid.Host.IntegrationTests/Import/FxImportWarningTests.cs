using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Import;

public class FxImportWarningTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static object Adults(NidApp app) => new Dictionary<string, Guid> { ["adult-1"] = app.AdultA.Value, ["adult-2"] = app.AdultB.Value };

    private const string Subscription = """[{"id":"sub-vpn","name":"VPN","ownership":[{"person":"adult-1","share_bp":10000}],"auto_renew":false}]""";

    private static Dictionary<string, string> File(string currency) => new()
    {
        ["persons"] = GoldenFile.Persons,
        ["subscriptions"] = Subscription,
        ["purchases"] = $$"""[{"id":"pu-2014","date":"2014-03-03","label":"VPN year","kind":"prepaid_term","price":{"amount":"39.99","currency":"{{currency}}"},"payer":{"type":"household"},"grants":[{"subscription_id":"sub-vpn","duration":{"count":1,"unit":"year"},"source":"paid"}]}]""",
    };

    [Fact]
    public async Task A_foreign_payment_without_a_rate_is_a_warning_on_its_path_and_does_not_block()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var validated = await new ImportClient(app).RunAsync(app.AdultA, Adults(app), File("USD"));

        Assert.Equal("validated", validated.State);
        var warning = Assert.Single(validated.Report!.Issues, i => i.Code == FxCodes.RateMissing);
        Assert.Equal(("warning", "purchases[0]"), (warning.Severity, warning.Path));
    }

    [Fact]
    public async Task With_a_rate_the_warning_is_gone()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
                .WriteAsync([new FxRate(new LocalDate(2014, 3, 3), Currency.Eur, 0.72m, FxRateSource.Ecb)], TestContext.Current.CancellationToken);

        var validated = await new ImportClient(app).RunAsync(app.AdultA, Adults(app), File("USD"));

        Assert.DoesNotContain(validated.Report!.Issues, i => i.Code == FxCodes.RateMissing);
    }

    [Fact]
    public async Task A_payment_in_the_reference_currency_needs_no_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var validated = await new ImportClient(app).RunAsync(app.AdultA, Adults(app), File("EUR"));
        Assert.DoesNotContain(validated.Report!.Issues, i => i.Code == FxCodes.RateMissing);
    }
}
