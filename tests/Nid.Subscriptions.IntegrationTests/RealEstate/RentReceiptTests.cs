using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using Nid.TestSupport;
using NodaTime;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

/// <summary>Rental receipts spec §4: <c>GET /properties/{id}/receipts?month=yyyy-mm</c>. Today is 2026-10-15; a tenancy from 2026-01-01 at 800 + 50.</summary>
public class RentReceiptTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string MonthRefused = "realestate.receipt_month", NoTenancy = "realestate.receipt_no_tenancy", TwoLeases = "realestate.receipt_two_leases";

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => NidApp.StartAsync(fixture, services => services.AddSingleton<IClock>(
            new FixedClock(new LocalDate(2026, 10, 15).At(new LocalTime(12, 0)).InUtc().ToInstant())));

    /// <summary>With 2025-Q2 and 2026-Q2 of the IRL, so a lease indexed on quarter 2 from 2025-09-01 is revised from 2026-09-01.</summary>
    private static async Task<NidApp> StartWithIrlAsync(PostgresFixture fixture)
    {
        var app = await StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IIrlStore>();
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2025, 2), 146.68m, new LocalDate(2025, 7, 16), new LocalDate(2025, 7, 21), "insee"), default);
        await store.UpsertAsync(new IrlValue(new IrlQuarter(2026, 2), 148.37m, new LocalDate(2026, 7, 12), new LocalDate(2026, 7, 13), "insee"), default);
        return app;
    }

    private static async Task<Guid> LetAsync(NidApp app, string startsOn = "2026-01-01", bool identity = true, string visibility = "shared", string? endsOn = null,
        bool indexed = false, string? rentsTrackedFrom = null)
    {
        var body = Home(app, "Locatif", visibility, app.AdultA);
        if (identity) body["postalAddress"] = "12 rue des Lilas\n75011 Paris";
        var property = await NewPropertyAsync(app, body);
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies", new
        {
            label = "T2", startsOn, endsOn, rentMonthly = "800", chargesMonthly = "50", deposit = "800", indexed, irlReferenceQuarter = indexed ? 2 : (int?)null, rentsTrackedFrom,
            tenantNames = identity ? "Mme Martin" : null, landlordName = identity ? "Alice et Bob" : null,
            landlordAddress = identity ? "1 rue A\n75001 Paris" : null,
        }));
        return property;
    }

    private static async Task PaidAsync(NidApp app, Guid property, string occurredOn, string amount, string category = "rent")
        => await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/flows",
            new { occurredOn, kind = "income", amount, category }));

    private static async Task<HttpResponseMessage> ReceiptAsync(NidApp app, Guid property, string? month = null, PersonId? actor = null)
        => await app.RawAsync(actor ?? app.AdultA, HttpMethod.Get, $"{Properties}/{property}/receipts" + (month is null ? "" : $"?month={month}"));

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string[] Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task A_settled_month_is_a_quittance_naming_everyone_and_offers_the_months_with_a_tenancy()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app);
        await PaidAsync(app, property, "2026-01-05", "850");

        var receipt = await OkAsync(await ReceiptAsync(app, property, "2026-01"));

        Assert.Equal(("Locatif", "EUR", "2026-01", "quittance"), (S(receipt, "propertyName"), S(receipt, "currency"), S(receipt, "month"), S(receipt, "kind")));
        Assert.Equal(("800.00", "50.00", "850.00", "850.00", "2026-01-05"),
            (S(receipt, "rent"), S(receipt, "charges"), S(receipt, "total"), S(receipt, "paid"), S(receipt, "paidOn")));
        Assert.Equal(("12 rue des Lilas\n75011 Paris", "Mme Martin", "Alice et Bob", "1 rue A\n75001 Paris"),
            (S(receipt, "postalAddress"), S(receipt, "tenantNames"), S(receipt, "landlordName"), S(receipt, "landlordAddress")));
        Assert.Empty(Strings(receipt, "missing"));
        Assert.Equal(["2026-10", "2026-09", "2026-08", "2026-07", "2026-06", "2026-05", "2026-04", "2026-03", "2026-02", "2026-01"],
            Strings(receipt, "months"));
    }

    [Fact]
    public async Task The_property_detail_lists_the_receipt_months()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app, startsOn: "2021-01-01");
        // Without cash in, the ledger is the last 12 months: from 2025-10-16, so October 2025.
        var months = Strings(await PropertyAsync(app, property), "receiptMonths");
        Assert.Equal(("2026-10", "2025-10", 13), (months[0], months[^1], months.Length));
        // The first rent recorded long after the lease's start: the rents are tracked from its month.
        await PaidAsync(app, property, "2025-03-05", "850");
        Assert.Equal("2025-03", Strings(await PropertyAsync(app, property), "receiptMonths")[^1]);
        Assert.Equal(Strings(await OkAsync(await ReceiptAsync(app, property)), "months"), Strings(await PropertyAsync(app, property), "receiptMonths"));

        // « Loyers suivis depuis » February 2025: February too.
        var tracked = await LetAsync(app, startsOn: "2021-01-01", rentsTrackedFrom: "2025-02");
        await PaidAsync(app, tracked, "2025-03-05", "850");
        Assert.Equal("2025-02", Strings(await PropertyAsync(app, tracked), "receiptMonths")[^1]);
        Assert.Equal("unpaid", S(await OkAsync(await ReceiptAsync(app, tracked, "2026-10")), "kind"));

        var bare = await NewPropertyAsync(app, Home(app));
        Assert.Empty(Strings(await PropertyAsync(app, bare), "receiptMonths"));
    }

    [Fact]
    public async Task A_partial_payment_gives_a_receipt_and_no_payment_gives_unpaid()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app);
        await PaidAsync(app, property, "2026-01-05", "400");

        var january = await OkAsync(await ReceiptAsync(app, property, "2026-01"));
        Assert.Equal(("receipt", "400.00", "850.00"), (S(january, "kind"), S(january, "paid"), S(january, "total")));
        var february = await OkAsync(await ReceiptAsync(app, property, "2026-02"));
        Assert.Equal(("unpaid", "0.00"), (S(february, "kind"), S(february, "paid")));
        Assert.Equal(JsonValueKind.Null, february.GetProperty("paidOn").ValueKind);
    }

    [Fact]
    public async Task Missing_identity_is_listed_and_nothing_is_invented()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app, identity: false);

        var receipt = await OkAsync(await ReceiptAsync(app, property, "2026-01"));

        Assert.Equal(["postal_address", "tenant_names", "landlord_name", "landlord_address"], Strings(receipt, "missing"));
        foreach (var name in new[] { "postalAddress", "tenantNames", "landlordName", "landlordAddress" })
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty(name).ValueKind);
    }

    [Fact]
    public async Task Without_a_month_the_receipt_is_the_latest_cash_in_s_else_the_current_month()
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app);
        Assert.Equal("2026-10", S(await OkAsync(await ReceiptAsync(app, property)), "month"));

        await PaidAsync(app, property, "2026-03-05", "850");
        Assert.Equal("2026-03", S(await OkAsync(await ReceiptAsync(app, property)), "month"));
    }

    [Fact]
    public async Task The_default_month_is_one_a_receipt_can_be_given_for()
    {
        await using var app = await StartAsync(fixture);
        // A rent paid 50 months ago is past the 36 months offered: the current month answers, and the old month is still given;
        // the month before it, in the lease but before the first rent tracked, is not.
        var old = await LetAsync(app, startsOn: "2022-01-01");
        await PaidAsync(app, old, "2022-08-05", "850");
        var current = await OkAsync(await ReceiptAsync(app, old));
        Assert.Equal("2026-10", S(current, "month"));
        Assert.Equal(36, Strings(current, "months").Length);
        Assert.Equal("2023-11", Strings(current, "months")[^1]);
        Assert.Equal("quittance", S(await OkAsync(await ReceiptAsync(app, old, "2022-08")), "kind"));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, MonthRefused, "month"), await RefusalAsync(await ReceiptAsync(app, old, "2022-07")));

        // A lease ended in August with a late payment in October: the newest covered month with cash, never a refusal.
        var ended = await LetAsync(app, endsOn: "2026-08-16");
        await PaidAsync(app, ended, "2026-06-05", "850");
        await PaidAsync(app, ended, "2026-10-05", "850");
        Assert.Equal("2026-06", S(await OkAsync(await ReceiptAsync(app, ended)), "month"));

        // No cash in at all and the current month uncovered: the newest covered month.
        var quiet = await LetAsync(app, endsOn: "2026-08-16");
        Assert.Equal("2026-08", S(await OkAsync(await ReceiptAsync(app, quiet)), "month"));
    }

    [Fact]
    public async Task A_revision_month_is_one_receipt_summing_both_links_with_the_last_link_s_names() // Review Focus #2
    {
        await using var app = await StartWithIrlAsync(fixture);
        var property = await LetAsync(app, startsOn: "2025-09-01", indexed: true);
        var first = (await PropertyAsync(app, property)).GetProperty("tenancies")[0].GetProperty("id").GetGuid();
        // The revision takes effect mid-September: the first link ends there, the second continues it at 800 × 148.37 ÷ 146.68.
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies/{first}/revision",
            new { effectiveOn = "2026-09-16" })).StatusCode);
        var last = (await PropertyAsync(app, property)).GetProperty("tenancies").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() != first);
        Assert.Equal(first, last.GetProperty("revisedFrom").GetGuid());
        Assert.Equal("809.22", S(last, "rentMonthly"));
        // The parties are edited on the last link: a new name, the landlord's address cleared.
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{property}/tenancies/{last.GetProperty("id").GetGuid()}", new
        {
            label = "T2", startsOn = "2026-09-16", rentMonthly = "809.22", chargesMonthly = "50", deposit = "800", tenantNames = "M. Durand", landlordAddress = "",
            version = last.GetProperty("version").GetUInt32(),
        })).StatusCode);

        var september = await OkAsync(await ReceiptAsync(app, property, "2026-09"));
        // Rent: 800 for 15 days and 809.22 for 15 days of 30; charges 50 throughout.
        Assert.Equal(("804.61", "50.00", "854.61"), (S(september, "rent"), S(september, "charges"), S(september, "total")));
        Assert.Equal(("M. Durand", JsonValueKind.Null), (S(september, "tenantNames"), september.GetProperty("landlordAddress").ValueKind));
        Assert.Equal(["landlord_address"], Strings(september, "missing"));
        Assert.Contains("2026-09", Strings(september, "months"));

        // A month of the first link alone still names the chain's last link.
        var august = await OkAsync(await ReceiptAsync(app, property, "2026-08"));
        Assert.Equal(("800.00", "M. Durand"), (S(august, "rent"), S(august, "tenantNames")));
        Assert.Equal(["landlord_address"], Strings(august, "missing"));
    }

    [Fact]
    public async Task The_month_rents_are_tracked_from_is_the_whole_chain_s()
    {
        await using var app = await StartWithIrlAsync(fixture);
        var property = await LetAsync(app, startsOn: "2020-09-01", indexed: true);
        // Rents recorded from March 2025 only, at 850, to October 2026.
        for (var on = new LocalDate(2025, 3, 5); on <= new LocalDate(2026, 10, 5); on = on.PlusMonths(1))
            await PaidAsync(app, property, on.ToString("yyyy-MM-dd", null), "850");
        var first = (await PropertyAsync(app, property)).GetProperty("tenancies")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies/{first}/revision",
            new { effectiveOn = "2026-09-16" })).StatusCode);
        async Task<JsonElement> Last() => (await PropertyAsync(app, property)).GetProperty("tenancies").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() != first);
        async Task<HttpStatusCode> TrackAsync(string month)
        {
            var last = await Last();
            return (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Properties}/{property}/tenancies/{last.GetProperty("id").GetGuid()}", new
            {
                label = "T2", startsOn = S(last, "startsOn"), rentMonthly = S(last, "rentMonthly"), chargesMonthly = "50", deposit = "800",
                rentsTrackedFrom = month, version = last.GetProperty("version").GetUInt32(),
            })).StatusCode;
        }
        // Automatic: from March 2025; 18 months at 850, September's two halves (854.61) and October revised (859.22), less 20 × 850.
        Assert.Equal(("2025-03", "13.83"), (Strings(await PropertyAsync(app, property), "receiptMonths")[^1], S((await PropertyAsync(app, property)).GetProperty("figures"), "arrears")));

        // Set on the last link, before its own start: every link of the chain takes it, and February 2025 is due.
        Assert.Equal(HttpStatusCode.OK, await TrackAsync("2025-02"));
        var detail = await PropertyAsync(app, property);
        Assert.All(detail.GetProperty("tenancies").EnumerateArray(), t => Assert.Equal("2025-02", S(t, "rentsTrackedFrom")));
        Assert.Equal(("2025-02", "863.83"), (Strings(detail, "receiptMonths")[^1], S(detail.GetProperty("figures"), "arrears")));

        // Cleared on the last link: cleared on the whole chain, the automatic start back.
        Assert.Equal(HttpStatusCode.OK, await TrackAsync(""));
        detail = await PropertyAsync(app, property);
        Assert.All(detail.GetProperty("tenancies").EnumerateArray(), t => Assert.Equal(JsonValueKind.Null, t.GetProperty("rentsTrackedFrom").ValueKind));
        Assert.Equal(("2025-03", "13.83"), (Strings(detail, "receiptMonths")[^1], S(detail.GetProperty("figures"), "arrears")));
    }

    [Fact]
    public async Task A_month_of_two_leases_that_are_not_one_revision_is_refused_and_not_offered()
    {
        await using var app = await StartAsync(fixture);
        // Mme Martin leaves on 16 June, M. Durand moves in the same day: a change of tenant, not a revision.
        var property = await LetAsync(app, endsOn: "2026-06-16");
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{property}/tenancies", new
        {
            label = "T2", startsOn = "2026-06-16", rentMonthly = "900", chargesMonthly = "50", tenantNames = "M. Durand",
        }));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, TwoLeases, "month"), await RefusalAsync(await ReceiptAsync(app, property, "2026-06")));
        var may = await OkAsync(await ReceiptAsync(app, property, "2026-05"));
        Assert.DoesNotContain("2026-06", Strings(may, "months"));
        Assert.Contains("2026-07", Strings(may, "months"));
        Assert.DoesNotContain("2026-06", Strings(await PropertyAsync(app, property), "receiptMonths"));
    }

    [Theory]
    [InlineData("2024-01", NoTenancy)]
    [InlineData("2026-11", MonthRefused)]
    [InlineData("nope", MonthRefused)]
    [InlineData("2025-12", NoTenancy)]
    public async Task A_month_out_of_range_or_without_tenancy_is_refused_on_the_month(string month, string code)
    {
        await using var app = await StartAsync(fixture);
        var property = await LetAsync(app);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, code, "month"), await RefusalAsync(await ReceiptAsync(app, property, month)));
    }

    [Fact]
    public async Task The_other_adult_s_private_property_gives_no_receipt_and_a_reader_of_a_shared_one_does()
    {
        await using var app = await StartAsync(fixture);
        var hidden = await LetAsync(app, visibility: "private");
        var shared = await LetAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await ReceiptAsync(app, hidden, "2026-01", app.AdultB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReceiptAsync(app, Guid.NewGuid(), "2026-01")).StatusCode);
        Assert.False((await PropertyAsync(app, shared, app.AdultB)).GetProperty("canWrite").GetBoolean());
        Assert.Equal("2026-01", S(await OkAsync(await ReceiptAsync(app, shared, "2026-01", app.AdultB)), "month"));
    }
}
