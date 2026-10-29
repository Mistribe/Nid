using Microsoft.EntityFrameworkCore;
using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;
using static Nid.Core.IntegrationTests.TaxEstimate.SaleContributionTests;
using Estimates = Nid.TestSupport.EnvelopeEstimateApi;

namespace Nid.Core.IntegrationTests.TaxEstimate;

/// <summary>
/// Sale tax spec §3–§5: each item's page and the household total « Si tout était vendu ». The host's today is 2025-10-15, so the
/// rules are 2025's (capital social levies 17,2 %); the household has two adults (allowance 9 200 €) unless a test says otherwise.
/// </summary>
public class SaleTaxTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<SaleTaxView> ViewAsync(NidApp app, ItemId? item = null, PersonId? reader = null, VisibilityScope scope = VisibilityScope.Me)
    {
        var (response, failure) = await app.InvokeAsync(reader ?? app.AdultA, new GetSaleTaxQuery(scope, item));
        Assert.Null(failure);
        return Assert.IsType<SaleTaxView>(response);
    }

    private static async Task<SaleItemView> PageAsync(NidApp app, ItemId item, PersonId? reader = null)
    {
        var view = await ViewAsync(app, item, reader);
        Assert.False(view.Total);
        Assert.Null(view.Totals);
        return Assert.Single(view.Items);
    }

    private static async Task<ItemId> PropertyAsync(
        NidApp app, string name, string type, string purchasedOn, string price, string notaryFees, string? value, string valuedOn = "2025-06-01")
    {
        var body = Home(app, name);
        (body["type"], body["purchaseDate"], body["purchasePrice"], body["notaryFees"]) = (type, purchasedOn, price, notaryFees);
        var id = await NewPropertyAsync(app, body);
        if (value is not null)
            await PostAsync(app, $"{Properties}/{id}/valuations", new { asOf = valuedOn, amount = value });
        return new ItemId(id);
    }

    private static Task WorksAsync(NidApp app, ItemId property, string on, string amount)
        => PostAsync(app, $"{Properties}/{property.Value}/flows", new { occurredOn = on, kind = "expense", category = "works", amount });

    private static object Opening(string on, string amount) => new { occurredOn = on, amount = Eur(amount) };

    private static (string? Gain, string? IncomeTax, string? SocialTax, string? Surtax, string? Net) Figures(SaleItemView item)
        => (item.Gain, item.IncomeTax, item.SocialTax, item.Surtax, item.Net);

    private static async Task MakeBDependentAsync(NidApp app)
    {
        await using var core = PostgresFixture.NewCoreContext(app.ConnectionString);
        await core.Persons.Where(p => p.Id == app.AdultB.Value).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Kind, "dependent").SetProperty(p => p.OidcSubject, (string?)null));
    }

    [Fact]
    public async Task A_secondary_residence_held_ten_years()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var house = await PropertyAsync(app, "Maison de vacances", "secondary_residence", "2015-06-01", "200000", "12000", "400000");
        await WorksAsync(app, house, "2018-04-01", "20000");

        var item = await PageAsync(app, house);

        // Fees: 12 000 real < 15 000 flat (7,5 %); works: 20 000 real < 30 000 flat (15 %, held more than 5 years).
        // Cost 200 000 + 15 000 + 30 000 = 245 000; gain 400 000 − 245 000 = 155 000; 10 full years: allowances 30 % and 8,25 %.
        // Income base 155 000 × 70 % = 108 500 → tax 19 % = 20 615; social base 155 000 × 91,75 % = 142 212,50 → 17,2 % = 24 460,55 → 24 461;
        // surtax 3 % × 108 500 − (110 000 − 108 500) / 10 = 3 255 − 150 = 3 105; net 400 000 − 20 615 − 24 461 − 3 105 = 351 819.
        Assert.Equal(("taxed", "secondary_residence", 10), (item.Status, item.Kind, item.YearsHeld));
        Assert.Equal(("properties", "Maison de vacances", "400000.00", new LocalDate(2025, 6, 1)), (item.Module, item.Name, item.Value, item.ValueOn));
        Assert.Equal(("155000.00", "20615.00", "24461.00", "3105.00", "351819.00"), Figures(item));
        var property = item.Property!;
        Assert.Equal(("245000.00", "flat", "flat", "30", "8.25"),
            (property.CostPrice, property.FeesMethod, property.WorksMethod, property.IncomeAllowancePct, property.SocialAllowancePct));
        Assert.Equal([new SaleOwnerView(app.AdultA.Value, "155000.00", "20615.00", "24461.00", "3105.00")], property.Owners);
        Assert.Equal(["sale_costs_not_deducted", "free_acquisition_fees_flat_not_allowed"], item.Notes);
        Assert.Null(item.Envelope);
    }

    [Fact]
    public async Task A_primary_residence_is_exempt()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var home = await PropertyAsync(app, "Maison", "primary_residence", "2020-06-15", "250000", "19000", "320000");

        var item = await PageAsync(app, home);

        Assert.Equal(("exempt", "primary_residence", "320000.00", 5), (item.Status, item.Kind, item.Value, item.YearsHeld));
        Assert.Equal((null, null, null, null, "320000.00"), Figures(item));
    }

    [Fact]
    public async Task A_rental_counts_the_works_flat_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var rental = await PropertyAsync(app, "Locatif", "rental", "2018-03-01", "200000", "21000", "260000");
        await WorksAsync(app, rental, "2020-05-01", "40000");

        var item = await PageAsync(app, rental);

        // Fees 21 000 real > 15 000 flat; works: the flat 30 000 only, the 40 000 real ignored. Cost 251 000, gain 9 000; 7 years:
        // allowances 12 % and 3,3 %. Tax 19 % × 7 920 = 1 504,80 → 1 505; social 17,2 % × 8 703 = 1 496,92 → 1 497; net 256 998.
        Assert.Equal(("taxed", 7), (item.Status, item.YearsHeld));
        Assert.Equal(("9000.00", "1505.00", "1497.00", "0.00", "256998.00"), Figures(item));
        Assert.Equal(("251000.00", "actual", "flat"), (item.Property!.CostPrice, item.Property.FeesMethod, item.Property.WorksMethod));
        Assert.Contains("rental_works_flat_only", item.Notes);
        Assert.DoesNotContain("free_acquisition_fees_flat_not_allowed", item.Notes); // the real fees, not the flat
    }

    [Fact]
    public async Task A_property_held_over_thirty_years_is_exempt()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var house = await PropertyAsync(app, "Maison de famille", "secondary_residence", "1990-01-01", "50000", "4000", "400000");

        var item = await PageAsync(app, house);

        // 35 full years: both allowances at 100 %, nothing is due.
        Assert.Equal(("exempt", 35), (item.Status, item.YearsHeld));
        Assert.Equal((null, null, null, null, "400000.00"), Figures(item));
        Assert.Null(item.Property);
        Assert.Empty(item.Notes);
    }

    [Fact]
    public async Task A_pea_at_four_and_six_years()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var young = await EnvelopeAsync(app, "PEA jeune", "pea", "2021-06-01", opening: Opening("2021-06-01", "10000"));
        await ValueAsync(app, young, "2025-10-01", "14000");
        var old = await EnvelopeAsync(app, "PEA ancien", "pea", "2019-03-01", opening: Opening("2019-03-01", "10000"));
        await FlowAsync(app, old, "2020-01-01", "contribution", "5000");
        await ValueAsync(app, old, "2023-01-01", "20000");
        await FlowAsync(app, old, "2023-01-10", "withdrawal", "3000");
        await ValueAsync(app, old, "2025-10-01", "25000");

        var four = await PageAsync(app, young);
        var six = await PageAsync(app, old);

        // Under 5 years: gain 4 000, tax 12,8 % = 512, social 17,2 % = 688, net 12 800.
        Assert.Equal(("taxed", 4, false), (four.Status, four.YearsHeld, four.Approximate));
        Assert.Equal(("4000.00", "512.00", "688.00", null, "12800.00"), Figures(four));
        Assert.Equal("10000.00", four.Envelope!.RemainingCapital);
        // Tracked from the opening day: no withdrawal before tracking.
        Assert.Equal(["pea_closes_under_five_years"], four.Notes);
        // The withdrawal of 3 000 out of 20 000 takes 15 % of the 15 000 paid in: 12 750 left; gain 12 250, social only 2 107.
        Assert.Equal(("taxed", 6), (six.Status, six.YearsHeld));
        Assert.Equal(("12250.00", "0.00", "2107.00", null, "22893.00"), Figures(six));
        Assert.Equal("12750.00", six.Envelope!.RemainingCapital);
        Assert.Empty(six.Notes);
    }

    // Opened 2016, tracked from 2020 with 40 000 of which 30 000 paid before 27/09/2017, then 20 000 in 2021; worth 80 000.
    private static async Task<ItemId> NineYearContractAsync(NidApp app)
    {
        var life = await EnvelopeAsync(app, "AV", "life_insurance", "2016-06-01", premiumsBefore2017: "30000", opening: Opening("2020-01-01", "40000"));
        await FlowAsync(app, life, "2021-01-01", "contribution", "20000");
        await ValueAsync(app, life, "2025-10-01", "80000");
        return life;
    }

    [Fact]
    public async Task A_life_insurance_at_nine_years_for_a_couple()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var life = await NineYearContractAsync(app);

        var view = await ViewAsync(app, life);
        var item = Assert.Single(view.Items);

        // Premiums 30 000 before 2017 and 30 000 after: gain 20 000, half each. A's premiums at 31/12/2024 are under 150 000, so the
        // post-2017 half is at 7,5 %. The 9 200 allowance goes to the pre-2017 part first: tax 7,5 % × 800 + 7,5 % × 10 000 = 810;
        // social 17,2 % × 20 000 = 3 440; net 80 000 − 810 − 3 440 = 75 750.
        Assert.Equal(("taxed", 9), (item.Status, item.YearsHeld));
        Assert.Equal(("20000.00", "810.00", "3440.00", null, "75750.00"), Figures(item));
        Assert.Equal(new EnvelopeSaleView("60000.00", "10000.00", "10000.00", "0.00", "9200.00"), item.Envelope);
        Assert.Equal(["life_insurance_social_levies_already_paid", "withdrawals_before_tracking_at_face_value"], item.Notes);
        Assert.Equal(["life_insurance_visible_contracts_only"], view.Notes);
    }

    [Fact]
    public async Task A_life_insurance_tracked_from_its_start_classes_each_payment_by_its_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var life = await EnvelopeAsync(app, "AV", "life_insurance", "2012-03-01");
        await FlowAsync(app, life, "2012-03-01", "contribution", "10000");
        await FlowAsync(app, life, "2019-01-01", "contribution", "10000");
        await ValueAsync(app, life, "2025-10-01", "30000");

        var item = await PageAsync(app, life);

        // No opening: the 2012 payment is pre-2017 by its date. Premiums 10 000 on each side: gain 10 000, 5 000 each; A's 20 000 at
        // 31/12/2024 are under 150 000. The 9 200 allowance takes the pre-2017 5 000, then 4 200: 7,5 % × 800 = 60; social 17,2 % ×
        // 10 000 = 1 720; net 30 000 − 60 − 1 720 = 28 220.
        Assert.Equal(("10000.00", "60.00", "1720.00", null, "28220.00"), Figures(item));
        Assert.Equal(new EnvelopeSaleView("20000.00", "5000.00", "5000.00", "0.00", "9200.00"), item.Envelope);
        // Nothing to declare: every premium is dated.
        Assert.Equal(["life_insurance_social_levies_already_paid"], item.Notes);
    }

    [Fact]
    public async Task A_life_insurance_at_nine_years_for_a_single_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var life = await NineYearContractAsync(app);
        await MakeBDependentAsync(app);

        var item = await PageAsync(app, life);

        // The 4 600 allowance: tax 7,5 % × 5 400 + 7,5 % × 10 000 = 1 155; net 80 000 − 1 155 − 3 440 = 75 405.
        Assert.Equal(("20000.00", "1155.00", "3440.00", null, "75405.00"), Figures(item));
        Assert.Equal("4600.00", item.Envelope!.AllowanceUsed);
    }

    [Fact]
    public async Task The_page_uses_the_whole_allowance_the_total_shares_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await EnvelopeAsync(app, "AV 1", "life_insurance", "2015-01-01", opening: Opening("2020-01-01", "10000"));
        await ValueAsync(app, first, "2025-10-01", "16000");
        var second = await EnvelopeAsync(app, "AV 2", "life_insurance", "2015-01-01", opening: Opening("2020-01-01", "10000"));
        await ValueAsync(app, second, "2025-10-01", "15000");

        var one = await PageAsync(app, first);
        var two = await PageAsync(app, second);
        var total = await ViewAsync(app);

        // Each page: the whole 9 200 covers its gain (6 000, 5 000), no income tax; social 1 032 and 860.
        Assert.Equal(("6000.00", "0.00", "1032.00", null, "14968.00"), Figures(one));
        Assert.Equal(("5000.00", "0.00", "860.00", null, "14140.00"), Figures(two));
        Assert.Equal(("6000.00", "5000.00"), (one.Envelope!.AllowanceUsed, two.Envelope!.AllowanceUsed));
        Assert.Contains("premiums_before_2017_unknown", one.Notes);
        // The total: 9 200 over 11 000 of gains, pro rata (5 018,18 and 4 181,82); tax 7,5 % × 981,82 = 73,64 → 74 and
        // 7,5 % × 818,18 = 61,36 → 61.
        Assert.True(total.Total);
        var items = total.Items.OrderBy(i => i.Name).ToList();
        Assert.Equal(("6000.00", "74.00", "1032.00", null, "14894.00"), Figures(items[0]));
        Assert.Equal(("5000.00", "61.00", "860.00", null, "14079.00"), Figures(items[1]));
        Assert.Equal(("5018.00", "4182.00"), (items[0].Envelope!.AllowanceUsed, items[1].Envelope!.AllowanceUsed));
        Assert.Equal(new SaleTotalsView("31000.00", "135.00", "1892.00", "0.00", "28973.00"), total.Totals);
        Assert.Equal(["total_not_sum_of_pages", "life_insurance_visible_contracts_only"], total.Notes);
    }

    [Fact]
    public async Task The_total_nets_securities()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var account = await EnvelopeAsync(app, "CTO", "brokerage", "2020-01-01", opening: Opening("2020-01-01", "10000"));
        await ValueAsync(app, account, "2025-10-01", "8000");
        var pea = await EnvelopeAsync(app, "PEA", "pea", "2022-01-01", opening: Opening("2022-01-01", "10000"));
        await ValueAsync(app, pea, "2025-10-01", "15000");

        var lossPage = await PageAsync(app, account);
        var gainPage = await PageAsync(app, pea);
        var total = await ViewAsync(app);

        // Alone: the account's loss of 2 000 pays nothing (approximate without priced lines), the PEA pays 12,8 % and 17,2 % of 5 000.
        Assert.Equal(("loss", true), (lossPage.Status, lossPage.Approximate));
        Assert.Equal(("-2000.00", "0.00", "0.00", null, "8000.00"), Figures(lossPage));
        Assert.Equal(["brokerage_scale_option", "approximate"], lossPage.Notes);
        Assert.Equal(("5000.00", "640.00", "860.00", null, "13500.00"), Figures(gainPage));
        // Netted: income tax on 3 000 (384) goes to the PEA; its social levies stay on its own gain.
        var items = total.Items.ToDictionary(i => i.Item);
        Assert.Equal(("-2000.00", "0.00", "0.00", null, "8000.00"), Figures(items[account]));
        Assert.Equal(("5000.00", "384.00", "860.00", null, "13756.00"), Figures(items[pea]));
        Assert.Equal(new SaleTotalsView("23000.00", "384.00", "860.00", "0.00", "21756.00"), total.Totals);
        Assert.Equal(["total_not_sum_of_pages"], total.Notes);
    }

    [Fact]
    public async Task Brokerage_lines_older_than_its_value_are_approximate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var account = await EnvelopeAsync(app, "CTO", "brokerage", "2020-01-01");
        var url = $"{Envelopes}/{account.Value}";
        var etf = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings",
            new { name = "ETF", kind = "etf", isin = Estimates.EtfIsin }));
        await PostAsync(app, $"{url}/holding-snapshots", new
        {
            asOf = "2025-06-30", recordValuation = true,
            lines = new object[] { new { holdingId = etf, units = "10", unitPrice = "100", value = Eur("1000"), costBasisPerUnit = "80" } },
        });
        await ValueAsync(app, account, "2025-09-30", "1200");

        var item = await PageAsync(app, account);

        // June's lines against September's value: gain 1 000 − 10 × 80 = 200, tax 12,8 % = 25,60 → 26, social 17,2 % = 34,40 → 34,
        // net 1 200 − 60 = 1 140.
        Assert.Equal(("1200.00", new LocalDate(2025, 9, 30), false, true), (item.Value, item.ValueOn, item.Estimated, item.Approximate));
        Assert.Equal(("200.00", "26.00", "34.00", null, "1140.00"), Figures(item));
        Assert.Equal(["brokerage_scale_option", "lines_older_than_value", "approximate"], item.Notes);
    }

    [Fact]
    public async Task A_pea_transfer_carries_its_capital()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var source = await EnvelopeAsync(app, "PEA ancienne banque", "pea", "2015-01-01", opening: Opening("2015-01-01", "10000"));
        await ValueAsync(app, source, "2024-12-31", "12000");
        var destination = await EnvelopeAsync(app, "PEA nouvelle banque", "pea", "2015-01-01");
        await TransferAsync(app, source, destination, "2025-01-10", "12000");
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Envelopes}/{source.Value}/close", new { closedOn = "2025-01-31" })).EnsureSuccessStatusCode();
        await ValueAsync(app, destination, "2025-10-01", "13000");

        var item = await PageAsync(app, destination);
        var total = await ViewAsync(app);

        // The 12 000 moved carries the 10 000 paid in, not 12 000: gain 3 000, social 17,2 % = 516.
        Assert.Equal(("10000.00", false), (item.Envelope!.RemainingCapital, item.Approximate));
        Assert.Equal(("3000.00", "0.00", "516.00", null, "12484.00"), Figures(item));
        Assert.Equal([destination], total.Items.Select(i => i.Item)); // the closed source is never sold
    }

    [Fact]
    public async Task A_transfer_from_a_source_the_reader_cannot_see_is_approximate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var hidden = await EnvelopeAsync(app, "PEA de B", "pea", "2015-01-01", [app.AdultB], "private", opening: Opening("2015-01-01", "4000"));
        var destination = await EnvelopeAsync(app, "PEA commun", "pea", "2015-01-01", [app.AdultA, app.AdultB]);
        await TransferAsync(app, hidden, destination, "2025-02-10", "4000", app.AdultB);
        await ValueAsync(app, destination, "2025-10-01", "5000");

        var item = await PageAsync(app, destination);

        Assert.Equal(("4000.00", true), (item.Envelope!.RemainingCapital, item.Approximate));
        Assert.Contains("approximate", item.Notes);
    }

    [Fact]
    public async Task Transfers_back_and_forth_the_same_day_are_approximate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await EnvelopeAsync(app, "PEA 1", "pea", "2015-01-01", opening: Opening("2015-01-01", "1000"));
        var second = await EnvelopeAsync(app, "PEA 2", "pea", "2015-01-01", opening: Opening("2015-01-01", "1000"));
        await TransferAsync(app, first, second, "2025-01-10", "500");
        await TransferAsync(app, second, first, "2025-01-10", "500");
        await ValueAsync(app, first, "2025-10-01", "1200");
        await ValueAsync(app, second, "2025-10-01", "1200");

        // Each source is met again while replaying the other: the transfer stays at its amount, flagged.
        Assert.All((await ViewAsync(app)).Items, i => Assert.True(i.Approximate));
    }

    [Fact]
    public async Task A_private_contract_of_another_adult_counts_nowhere()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await EnvelopeAsync(app, "AV commune", "life_insurance", "2015-01-01", [app.AdultB], premiumsBefore2017: "0",
            opening: Opening("2020-01-01", "100000"));
        await PostAsync(app, $"{Envelopes}/{shared.Value}/valuations", new { asOf = "2025-10-01", amount = Eur("110000") }, app.AdultB);
        var hidden = await EnvelopeAsync(app, "AV de B", "life_insurance", "2015-01-01", [app.AdultB], "private", premiumsBefore2017: "0",
            opening: Opening("2020-01-01", "200000"));
        await PostAsync(app, $"{Envelopes}/{hidden.Value}/valuations", new { asOf = "2025-10-01", amount = Eur("260000") }, app.AdultB);

        var page = await PageAsync(app, shared);
        var total = await ViewAsync(app);
        var theirs = await PageAsync(app, shared, app.AdultB);

        // For A, B holds 100 000 of premiums: all under the threshold. Gain 10 000 at 7,5 % after 9 200: 60; social 1 720.
        Assert.Equal(("10000.00", "60.00", "1720.00", null, "108220.00"), Figures(page));
        Assert.Equal([shared], total.Items.Select(i => i.Item));
        Assert.Equal(new SaleTotalsView("110000.00", "60.00", "1720.00", "0.00", "108220.00"), total.Totals);
        // B sees their 300 000: half the gain over the threshold, so 5 000 at 7,5 % (allowance) and 800 at 12,8 % = 102.
        Assert.Equal(("10000.00", "102.00", "1720.00", null, "108178.00"), Figures(theirs));
        Assert.Equal(new EnvelopeSaleView("100000.00", "0.00", "5000.00", "5000.00", "9200.00"), theirs.Envelope);
    }

    [Fact]
    public async Task Findings_give_no_figure()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var undated = await EnvelopeAsync(app, "PEA sans date", "pea", opening: Opening("2020-01-01", "1000"));
        await ValueAsync(app, undated, "2025-10-01", "1500");
        var dollars = new ItemId(await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, Envelopes, new
        {
            displayName = "Compte US", kind = "brokerage", currency = "USD", visibility = "shared", openedOn = "2020-01-01",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        })));
        var unvalued = await EnvelopeAsync(app, "AV sans valeur", "life_insurance", "2015-01-01", opening: Opening("2020-01-01", "1000"));
        var land = await PropertyAsync(app, "Terrain", "land", "2010-01-01", "50000", "4000", null);

        var items = (await ViewAsync(app)).Items.ToDictionary(i => i.Item);

        Assert.Equal(("finding", "opening_date_unknown"), (items[undated].Status, items[undated].Finding));
        Assert.Equal(("finding", "other_currency"), (items[dollars].Status, items[dollars].Finding));
        Assert.Equal(("finding", "no_value"), (items[unvalued].Status, items[unvalued].Finding));
        Assert.Equal(("finding", "no_value"), (items[land].Status, items[land].Finding));
        Assert.All(items.Values, i => Assert.Equal((null, null, null, null, null, null), (i.Value, i.Gain, i.IncomeTax, i.SocialTax, i.Surtax, i.Net)));
    }

    [Fact]
    public async Task A_per_is_not_estimated_and_savings_are_not_taxable()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var per = await EnvelopeAsync(app, "PER", "per", "2020-01-01", opening: Opening("2020-01-01", "5000"));
        await ValueAsync(app, per, "2025-10-01", "6000");
        var savings = await EnvelopeAsync(app, "Livret", "savings", opening: Opening("2020-01-01", "3000"));
        await ValueAsync(app, savings, "2025-10-01", "3100");

        var total = await ViewAsync(app);
        var items = total.Items.ToDictionary(i => i.Item);

        Assert.Equal(("not_estimated", "6000.00", null), (items[per].Status, items[per].Value, items[per].Net));
        Assert.Equal(("not_taxable", "3100.00", "3100.00"), (items[savings].Status, items[savings].Value, items[savings].Net));
        // Only what has a net enters the totals.
        Assert.Equal(new SaleTotalsView("3100.00", "0.00", "0.00", "0.00", "3100.00"), total.Totals);
    }

    [Fact]
    public async Task An_estimated_envelope_uses_its_estimate_and_says_so()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var account = await EnvelopeAsync(app, "CTO", "brokerage", "2020-01-01");
        var url = $"{Envelopes}/{account.Value}";
        var etf = await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings",
            new { name = "ETF", kind = "etf", isin = Estimates.EtfIsin }));
        await PostAsync(app, $"{url}/holding-snapshots", new
        {
            asOf = "2025-09-30", recordValuation = true,
            lines = new object[] { new { holdingId = etf, units = "10", unitPrice = "100", value = Eur("1000"), costBasisPerUnit = "80" } },
        });
        (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { pricesEnabled = true })).EnsureSuccessStatusCode();
        await Estimates.PriceAsync(app, Estimates.EtfIsin, 110m, Estimates.Day);

        var item = await PageAsync(app, account);

        // 10 units at 110 against a PRU of 80: gain 300, tax 12,8 % = 38,40 → 38, social 17,2 % = 51,60 → 52.
        Assert.Equal(("1100.00", Estimates.Day, true, false), (item.Value, item.ValueOn, item.Estimated, item.Approximate));
        Assert.Equal(("300.00", "38.00", "52.00", null, "1010.00"), Figures(item));
        Assert.Equal(["brokerage_scale_option", "value_estimated"], item.Notes);
    }

    [Fact]
    public async Task An_unknown_or_closed_item_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var closed = await EnvelopeAsync(app, "PEA clos", "pea", "2015-01-01", opening: Opening("2015-01-01", "1000"));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Envelopes}/{closed.Value}/close", new { closedOn = "2025-01-31" })).EnsureSuccessStatusCode();

        foreach (var item in new[] { new ItemId(Guid.CreateVersion7()), closed })
        {
            var (_, failure) = await app.InvokeAsync(app.AdultA, new GetSaleTaxQuery(VisibilityScope.Me, item));
            Assert.IsType<NotFoundFailure>(failure);
        }
    }

    private static Task ValueAsAsync(NidApp app, ItemId envelope, string on, string amount, PersonId actor)
        => PostAsync(app, $"{Envelopes}/{envelope.Value}/valuations", new { asOf = on, amount = Eur(amount) }, actor);

    [Fact]
    public async Task A_household_total_counts_the_readers_private_contracts_and_sources()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await EnvelopeAsync(app, "AV commune", "life_insurance", "2015-01-01", premiumsBefore2017: "0", opening: Opening("2020-01-01", "100000"));
        await ValueAsync(app, shared, "2025-10-01", "110000");
        var mine = await EnvelopeAsync(app, "AV perso", "life_insurance", "2015-01-01", visibility: "private", premiumsBefore2017: "0",
            opening: Opening("2020-01-01", "200000"));
        await ValueAsync(app, mine, "2025-10-01", "200000");
        var source = await EnvelopeAsync(app, "PEA perso", "pea", "2015-01-01", visibility: "private", opening: Opening("2015-01-01", "10000"));
        await ValueAsync(app, source, "2024-12-31", "12000");
        var pea = await EnvelopeAsync(app, "PEA commun", "pea", "2015-01-01");
        await TransferAsync(app, source, pea, "2025-01-10", "12000");
        await ValueAsync(app, pea, "2025-10-01", "13000");

        var total = await ViewAsync(app, scope: VisibilityScope.Household);

        Assert.Equal([shared, pea], total.Items.Select(i => i.Item).OrderBy(i => i == pea));
        var items = total.Items.ToDictionary(i => i.Item);
        // A holds 300 000 at 31/12/2024 with the private contract: half of the 10 000 gain over the threshold, 5 000 at 7,5 % and
        // 5 000 at 12,8 %; the 9 200 allowance takes the first and 4 200 of the second: 12,8 % × 800 = 102.4 → 102.
        Assert.Equal(("10000.00", "102.00", "1720.00", null, "108178.00"), Figures(items[shared]));
        // The private source is replayed: 10 000 carried, exact.
        Assert.Equal(("10000.00", false), (items[pea].Envelope!.RemainingCapital, items[pea].Approximate));
    }

    [Fact]
    public async Task A_transfer_back_later_carries_its_capital_both_ways()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var first = await EnvelopeAsync(app, "PEA A", "pea", "2015-01-01", opening: Opening("2015-01-01", "10000"));
        await ValueAsync(app, first, "2024-12-31", "20000");
        var second = await EnvelopeAsync(app, "PEA B", "pea", "2015-01-01");
        await TransferAsync(app, first, second, "2025-01-10", "5000");
        await ValueAsync(app, second, "2025-02-01", "6000");
        await TransferAsync(app, second, first, "2025-02-10", "3000");
        await ValueAsync(app, first, "2025-10-01", "20000");
        await ValueAsync(app, second, "2025-10-01", "3500");

        var items = (await ViewAsync(app)).Items.ToDictionary(i => i.Item);

        // A → B on 10/01: 5 000 of 20 000 takes a quarter of A's 10 000, so 2 500 carried and 7 500 left. B → A on 10/02: 3 000 of
        // 6 000 takes half of B's 2 500, so 1 250 carried back: A 8 750 (gain 11 250, social 17,2 % = 1 935), B 1 250 (gain 2 250, 387).
        Assert.Equal(("8750.00", false), (items[first].Envelope!.RemainingCapital, items[first].Approximate));
        Assert.Equal(("11250.00", "0.00", "1935.00", null, "18065.00"), Figures(items[first]));
        Assert.Equal(("1250.00", false), (items[second].Envelope!.RemainingCapital, items[second].Approximate));
        Assert.Equal(("2250.00", "0.00", "387.00", null, "3113.00"), Figures(items[second]));
    }

    [Fact]
    public async Task The_threshold_counts_the_premiums_held_at_the_end_of_last_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // A: a contract closed in March still held 100 000 at 31/12/2024.
        var a = await EnvelopeAsync(app, "AV de A", "life_insurance", "2015-01-01", premiumsBefore2017: "0", opening: Opening("2020-01-01", "100000"));
        await ValueAsync(app, a, "2025-10-01", "110000");
        var closed = await EnvelopeAsync(app, "AV close", "life_insurance", "2015-01-01", premiumsBefore2017: "0", opening: Opening("2020-01-01", "100000"));
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Envelopes}/{closed.Value}/close", new { closedOn = "2025-03-01" })).EnsureSuccessStatusCode();
        // B: 100 000 more paid this year, not held at 31/12/2024.
        var b = await EnvelopeAsync(app, "AV de B", "life_insurance", "2015-01-01", [app.AdultB], premiumsBefore2017: "0", opening: Opening("2020-01-01", "100000"));
        await FlowAsync(app, b, "2025-02-01", "contribution", "100000", app.AdultB);
        await ValueAsAsync(app, b, "2025-10-01", "210000", app.AdultB);
        var young = await EnvelopeAsync(app, "AV récente", "life_insurance", "2018-01-01", opening: Opening("2018-01-01", "1000"));
        await ValueAsync(app, young, "2025-10-01", "1000");

        var ofA = await PageAsync(app, a);
        var ofB = await PageAsync(app, b);
        var even = await PageAsync(app, young);

        // A held 200 000: a quarter of the gain over the threshold, 7 500 at 7,5 % and 2 500 at 12,8 %; after 9 200: 12,8 % × 800 = 102.
        Assert.Equal(("10000.00", "102.00"), (ofA.Gain, ofA.IncomeTax));
        // B held 100 000: all at 7,5 %, 7,5 % × 800 = 60.
        Assert.Equal(("10000.00", "60.00"), (ofB.Gain, ofB.IncomeTax));
        // Opened after 27/09/2017: no premium can predate 2017. A gain of zero is taxed at nothing, not a loss.
        Assert.DoesNotContain("premiums_before_2017_unknown", even.Notes);
        Assert.Equal(("taxed", "0.00", "0.00", "0.00"), (even.Status, even.Gain, even.IncomeTax, even.SocialTax));
    }
}
