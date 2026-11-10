using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using static Nid.TestSupport.LoanApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>Issue #12: the yearly review's loans (the app clock is fixed at 2025-10-15, so 2024 is a full year).</summary>
public class LoansReviewTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Start = new(2023, 12, 31), End = new(2024, 12, 31);

    private static async Task<T> InScopeAsync<T>(NidApp app, Func<IServiceProvider, Task<T>> read)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(app.AdultA);
        return await read(scope.ServiceProvider);
    }

    private static Task<LoansReviewPart> ReviewAsync(NidApp app) => InScopeAsync(app, async sp =>
    {
        var review = await sp.GetRequiredService<ILoansReview>()
            .ReviewAsync(app.AdultA, VisibilityScope.Household, Start, End, TestContext.Current.CancellationToken);
        Assert.True(review.TryGetValue(out var part));
        return part!;
    });

    private static Task<bool> ArchiveAsync(NidApp app, Guid id, LocalDate on) => InScopeAsync(app, async sp =>
        (await sp.GetRequiredService<IItemRegistry>().ArchiveAsync(new ItemId(id), Instant.FromUtc(on.Year, on.Month, on.Day, 10, 0), TestContext.Current.CancellationToken)).IsSuccess);

    private static decimal D(string? amount) => decimal.Parse(amount!, CultureInfo.InvariantCulture);

    /// <summary>12 000 at no interest over 12 payments of 1 000, from <paramref name="year"/>-01-15.</summary>
    private static Dictionary<string, object?> Yearly(NidApp app, string name, int year)
    {
        var body = Short(app, name);
        body["disbursedOn"] = $"{year}-01-01";
        body["firstPaymentDate"] = $"{year}-01-15";
        return body;
    }

    [Fact]
    public async Task A_loan_year_matches_its_schedule_line()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Golden(app);
        body["insurances"] = new[] { new { mode = "fixed_monthly", monthlyAmount = "30", startsOn = "2020-11-15" } };
        var id = await NewAsync(app, body);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments",
            new { onDate = "2024-03-20", amount = "5000", effect = "reduce_term" }));

        var part = await ReviewAsync(app);
        var years = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule")))
            .GetProperty("years").EnumerateArray().ToList();
        var year = years.Single(y => y.GetProperty("year").GetInt32() == 2024);
        var before = years.Single(y => y.GetProperty("year").GetInt32() == 2023);

        var row = Assert.Single(part.Loans);
        Assert.Equal((D(S(year, "interest")), 360m, D(S(year, "principal")) + 5000m, 0m),
            (row.Interest!.Value, row.Insurance!.Value, row.CapitalRepaid!.Value, row.Fees!.Value));
        Assert.Equal((D(S(before, "balance")), D(S(year, "balance"))), (row.StartBalance!.Value, row.EndBalance!.Value));
        Assert.Equal((row.Interest, row.CapitalRepaid, row.EndBalance), (part.Interest, part.CapitalRepaid, part.EndBalance));
        Assert.Empty(part.MissingCurrencies);
    }

    [Fact]
    public async Task An_early_repayment_then_archive_counts_and_ends_at_zero()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Yearly(app, "Prêt travaux", 2024));
        // After the June payment 6 000 are left: repaid in full, then the loan is archived.
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/repayments",
            new { onDate = "2024-06-20", amount = "6000", effect = "reduce_term" }));
        Assert.True(await ArchiveAsync(app, id, new LocalDate(2024, 7, 1)));

        var row = Assert.Single((await ReviewAsync(app)).Loans);

        Assert.Equal((12_000m, 0m, 0m, new LocalDate(2024, 7, 1)),
            (row.CapitalRepaid!.Value, row.StartBalance!.Value, row.EndBalance!.Value, row.ArchivedOn));
    }

    [Fact]
    public async Task An_archived_loan_counts_to_its_last_day_then_leaves_the_books()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Yearly(app, "Prêt travaux", 2024));
        Assert.True(await ArchiveAsync(app, id, new LocalDate(2024, 7, 15)));

        var row = Assert.Single((await ReviewAsync(app)).Loans);

        // The July payment falls on its archive day: six payments counted; it leaves the books with what it still owed.
        Assert.Equal((6_000m, 0m, new LocalDate(2024, 7, 15)), (row.CapitalRepaid!.Value, row.EndBalance!.Value, row.ArchivedOn));
    }

    [Fact]
    public async Task A_loan_archived_on_the_first_day_is_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app, "Prêt vendu"));
        Assert.True(await ArchiveAsync(app, id, new LocalDate(2024, 1, 1)));
        var after = await NewAsync(app, Golden(app));
        Assert.True(await ArchiveAsync(app, after, new LocalDate(2025, 3, 1)));

        var row = Assert.Single((await ReviewAsync(app)).Loans);

        // Archived after the year: counted in full, still on the books.
        Assert.Equal(("Prêt maison", (LocalDate?)null), (row.Name, row.ArchivedOn));
        Assert.NotEqual(0m, row.EndBalance);
    }

    [Fact]
    public async Task A_loan_ended_before_the_year_is_left_out()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await NewAsync(app, Yearly(app, "Ancien prêt", 2022));
        await NewAsync(app, Yearly(app, "Futur prêt", 2025));
        await NewAsync(app, Golden(app));

        var part = await ReviewAsync(app);

        Assert.Equal(["Prêt maison"], part.Loans.Select(l => l.Name));
    }

    [Fact]
    public async Task Fees_paid_in_the_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, Golden(app));
        foreach (var (kind, amount, onDate) in new[] { ("file", "1000", "2020-10-15"), ("guarantee", "300", "2024-05-02"), ("other", "40", "2025-01-10") })
            await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Loans}/{id}/fees", new { kind, amount, onDate }));

        var part = await ReviewAsync(app);

        Assert.Equal(300m, part.Fees);
        Assert.Equal(300m, Assert.Single(part.Loans).Fees);
    }

    [Fact]
    public async Task A_loan_in_another_currency_converts_its_balances_at_each_end()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var body = Golden(app);
        body["currency"] = "USD";
        var id = await NewAsync(app, body);

        var unconverted = await ReviewAsync(app);
        Assert.Equal((null, null, null), (unconverted.Interest, unconverted.StartBalance, unconverted.EndBalance));
        Assert.Equal(["USD"], unconverted.MissingCurrencies);

        // Rates per USD: a dollar is 0.5 € at the start, 0.8 € at the end.
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync(
                [new FxRate(Start, Currency.Eur, 0.5m, FxRateSource.Ecb), new FxRate(End, Currency.Eur, 0.8m, FxRateSource.Ecb)],
                TestContext.Current.CancellationToken);
        var part = await ReviewAsync(app);
        var years = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Loans}/{id}/schedule")))
            .GetProperty("years").EnumerateArray().ToList();
        decimal At(int y, string field) => D(S(years.Single(x => x.GetProperty("year").GetInt32() == y), field));
        static decimal Cents(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

        var row = Assert.Single(part.Loans);
        Assert.Empty(part.MissingCurrencies);
        Assert.Equal((Cents(At(2024, "interest") * 0.8m), Cents(At(2023, "balance") * 0.5m), Cents(At(2024, "balance") * 0.8m)),
            (row.Interest!.Value, row.StartBalance!.Value, row.EndBalance!.Value));
    }
}
