using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Application.Reconciliation;
using Nid.Banking.Contracts;
using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using Nid.TestSupport;
using UnambitiousFx.Functional.Failures;
using static Nid.Childcare.IntegrationTests.ChildcareApi;

namespace Nid.Childcare.IntegrationTests;

/// <summary>Childcare spec §6, decisions 19–23: the validated statements in the tax, the dashboard and the bank. Today is 2025-10-15.</summary>
public class ChildcareIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task ProfileAsync(NidApp app)
        => Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=household", new
        {
            dependents = 1, childrenUnder6 = 1, childcareOutsideHome = "9999", pinel = Array.Empty<object>(),
        })).StatusCode);

    private static string TaxBase(System.Text.Json.JsonElement month)
    {
        var d = month.GetProperty("figures").GetProperty("declaration");
        decimal Of(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
        // Spec §6: net pay + upkeep − CMG, as the server writes it.
        return (Of(d.GetProperty("netPay").GetString()!) + Of(d.GetProperty("upkeep").GetString()!) - Of(month.GetProperty("cmg").GetString()!))
            .ToString("0.00", CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Validated_statements_replace_the_profile_amount()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProfileAsync(app);
        var arrangement = await CreateAsync(app);
        var september = await ValidateAsync(app, arrangement, "2025-09");

        var tax = await GetAsync(app, "/api/v1/tax?year=2025&scope=household");
        Assert.Equal(TaxBase(september), tax.GetProperty("inputs").GetProperty("childcareOutsideHome").GetString());
        Assert.Contains(tax.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "childcare_from_statements");

        var next = await GetAsync(app, "/api/v1/tax?year=2026&scope=household");
        Assert.Equal("9999.00", next.GetProperty("inputs").GetProperty("childcareOutsideHome").GetString());
    }

    [Fact]
    public async Task Reopened_month_leaves_the_tax()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProfileAsync(app);
        var arrangement = await CreateAsync(app);
        await ValidateAsync(app, arrangement, "2025-09");
        await ReopenAsync(app, arrangement, "2025-09");

        var tax = await GetAsync(app, "/api/v1/tax?year=2025&scope=household");
        Assert.Equal("9999.00", tax.GetProperty("inputs").GetProperty("childcareOutsideHome").GetString());
        Assert.DoesNotContain(tax.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "childcare_from_statements");
    }

    private static async Task<System.Text.Json.JsonElement?> ChildcareSourceAsync(NidApp app)
    {
        var dashboard = await GetAsync(app, "/api/v1/dashboard?scope=household");
        return dashboard.GetProperty("commitmentSources").EnumerateArray()
            .Select(s => (System.Text.Json.JsonElement?)s).FirstOrDefault(s => s!.Value.GetProperty("source").GetString() == "childcare");
    }

    [Fact]
    public async Task Current_month_is_an_estimated_commitment()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var october = await GetAsync(app, $"{arrangement}/statements/2025-10");
        var source = (await ChildcareSourceAsync(app))!.Value;
        Assert.True(source.GetProperty("estimated").GetBoolean());
        Assert.Equal(october.GetProperty("figures").GetProperty("cost").GetProperty("amount").GetString(), source.GetProperty("amortized").GetProperty("amount").GetString());
    }

    /// <summary>#30, #31: the contributions the CMG leaves to the household count in the cost, the year and the next months' estimate.</summary>
    [Fact]
    public async Task Contributions_left_to_the_household_count_in_the_cost()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        static decimal Amount(System.Text.Json.JsonElement e) => decimal.Parse(e.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var draft = Amount((await GetAsync(app, $"{arrangement}/statements/2025-09")).GetProperty("figures").GetProperty("cost"));

        var september = await ValidateAsync(app, arrangement, "2025-09", cmg: "100", contributions: "40");
        Assert.Equal("40.00", september.GetProperty("contributions").GetString());
        Assert.Equal(draft - 100m + 40m, Amount(september.GetProperty("figures").GetProperty("cost")));

        var year = await GetAsync(app, $"{arrangement}/summary?year=2025");
        Assert.Equal(40m, Amount(year.GetProperty("contributions")));
        Assert.Equal(Amount(year.GetProperty("netPay")) + Amount(year.GetProperty("upkeep")) + 40m - 100m, Amount(year.GetProperty("retained")));

        // October is not declared yet: the dashboard estimates its CMG and its contributions from September.
        var october = Amount((await GetAsync(app, $"{arrangement}/statements/2025-10")).GetProperty("figures").GetProperty("cost"));
        Assert.Equal(october - 100m + 40m, Amount((await ChildcareSourceAsync(app))!.Value.GetProperty("amortized")));
    }

    [Fact]
    public async Task Validated_month_is_not_estimated()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        await ValidateAsync(app, arrangement, "2025-09");
        var october = await ValidateAsync(app, arrangement, "2025-10");
        var source = (await ChildcareSourceAsync(app))!.Value;
        Assert.False(source.GetProperty("estimated").GetBoolean());
        Assert.Equal(october.GetProperty("figures").GetProperty("cost").GetProperty("amount").GetString(), source.GetProperty("amortized").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Inconsistent_draft_falls_back_to_the_last_validated()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var september = await ValidateAsync(app, arrangement, "2025-09");
        var url = $"{arrangement}/statements/2025-10";
        var read = await GetAsync(app, url);
        var days = read.GetProperty("days").EnumerateArray().Select(d => new
        {
            date = d.GetProperty("date").GetString(), present = d.GetProperty("date").GetString() != "2025-10-06" && d.GetProperty("present").GetBoolean(),
            arrival = d.GetProperty("date").GetString() == "2025-10-06" ? null : d.GetProperty("arrival").GetString(),
            departure = d.GetProperty("date").GetString() == "2025-10-06" ? null : d.GetProperty("departure").GetString(),
            lunch = d.GetProperty("date").GetString() != "2025-10-06" && d.GetProperty("lunch").GetBoolean(),
            absence = d.GetProperty("date").GetString() == "2025-10-06" ? "family" : d.GetProperty("absence").GetString(),
        }).ToList();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { days, mileage = "0", cmg = (string?)null, version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"{arrangement}/gaps",
            new { gaps = new[] { new { monday = "2025-10-06", weeks = 1 } } })).StatusCode);

        var source = (await ChildcareSourceAsync(app))!.Value;
        Assert.True(source.GetProperty("estimated").GetBoolean());
        Assert.Equal(september.GetProperty("figures").GetProperty("cost").GetProperty("amount").GetString(), source.GetProperty("amortized").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Closed_arrangement_commits_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/close", new { on = "2025-09-30" })).StatusCode);
        Assert.Null(await ChildcareSourceAsync(app));
    }

    private static async Task<IReadOnlyList<ExpectedCharge>> ChargesAsync(NidApp app, PersonId reader, bool writable = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader);
        var from = new LocalDate(2025, 10, 1);
        var to = new LocalDate(2025, 10, 31);
        if (writable)
            return await scope.ServiceProvider.GetRequiredService<ExpectedChargeReader>().WritableAsync(reader, from, to, CancellationToken.None);
        var source = scope.ServiceProvider.GetServices<IExpectedCharges>().Single(s => s.GetType().Name == "ChildcareCharges");
        Assert.True((await source.ChargesAsync(reader, from, to, CancellationToken.None)).TryGetValue(out var charges));
        return charges!;
    }

    [Fact]
    public async Task Validated_month_is_an_expected_debit()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var september = await ValidateAsync(app, arrangement, "2025-09");
        var charge = Assert.Single(await ChargesAsync(app, app.AdultA));
        Assert.Equal("statement:2025-09", charge.Key);
        Assert.Equal(ChargeKind.Childcare, charge.Kind);
        Assert.Equal(new LocalDate(2025, 10, 17), charge.DueOn); // validated on 2025-10-15, the test clock
        Assert.Equal(september.GetProperty("figures").GetProperty("cost").GetProperty("amount").GetString(), charge.Amount.AmountText);
        Assert.False(charge.Credit);
        Assert.Contains("PAJEMPLOI", charge.LabelHints);
    }

    [Fact]
    public async Task Reopened_month_expects_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        await ValidateAsync(app, arrangement, "2025-09");
        await ReopenAsync(app, arrangement, "2025-09");
        Assert.Empty(await ChargesAsync(app, app.AdultA));
    }

    [Fact]
    public async Task Shared_reader_cannot_link_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        await ValidateAsync(app, arrangement, "2025-09");
        Assert.Contains(await ChargesAsync(app, app.AdultA, writable: true), c => c.Kind == ChargeKind.Childcare);
        Assert.DoesNotContain(await ChargesAsync(app, app.AdultB, writable: true), c => c.Kind == ChargeKind.Childcare);
    }

    [Fact]
    public void A_childcare_charge_opens_the_arrangement() => Assert.Equal("childcare", BankingNotificationSource.ModuleOf(ChargeKind.Childcare));

    [Fact]
    public async Task A_Pajemploi_line_links_to_the_statement()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        var september = await ValidateAsync(app, arrangement, "2025-09");
        var cost = decimal.Parse(september.GetProperty("figures").GetProperty("cost").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

        await BankingApi.ConnectAsync(app, app.AdultA, "code-a", BankingApi.Account("uid-1", "hash-1", null, "Courant"));
        var account = (await BankingApi.AccountsAsync(app, app.AdultA)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var envelope = await app.NewEnvelopeAsync("Courant", app.AdultA, Visibility.Shared);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Patch, $"{BankingApi.Root}/accounts/{account}", new { envelopeId = envelope.Value })).StatusCode);
        Guid line;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
            await store.AddAsync(account, [new NewBankTransaction("p-1", new LocalDate(2025, 10, 17), null, -(cost + 1m), Currency.Eur, "PRLV SEPA URSSAF PAJEMPLOI", "Urssaf", null, "api")], TestContext.Current.CancellationToken);
            line = (await store.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken)).Single().Id;
        }

        var item = Guid.Parse(arrangement[(arrangement.LastIndexOf('/') + 1)..]);
        var linked = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{BankingApi.Root}/transactions/{line}/link", new { module = "childcare", itemId = item, key = "statement:2025-09" });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var link = (await JsonAsync(linked)).GetProperty("link");
        Assert.Equal("childcare", link.GetProperty("module").GetString());
        Assert.Equal("price_differs", link.GetProperty("outcome").GetString()); // one euro more than the statement's cost
    }

    [Fact]
    public async Task Closing_at_the_month_end_keeps_the_commitment()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await CreateAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{arrangement}/close", new { on = "2025-10-31" })).StatusCode);
        Assert.NotNull(await ChildcareSourceAsync(app));
    }

    [Fact]
    public async Task A_shared_reader_sees_the_commitment_and_the_tax()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProfileAsync(app);
        var arrangement = await CreateAsync(app);
        await ValidateAsync(app, arrangement, "2025-09");
        var dashboard = await GetAsync(app, "/api/v1/dashboard?scope=household", app.AdultB);
        Assert.Contains(dashboard.GetProperty("commitmentSources").EnumerateArray(), s => s.GetProperty("source").GetString() == "childcare");
        var tax = await GetAsync(app, "/api/v1/tax?year=2025&scope=household", app.AdultB);
        Assert.Contains(tax.GetProperty("notices").EnumerateArray(), n => n.GetProperty("code").GetString() == "childcare_from_statements");
    }

    private static IReadOnlyList<WeekdaySlot> Slots(IsoDayOfWeek[] days, LocalTime? tuesdayDeparture = null)
        => [.. days.Select(d => new WeekdaySlot(d, new LocalTime(8, 15), d == IsoDayOfWeek.Tuesday ? tuesdayDeparture ?? new LocalTime(18, 0) : new LocalTime(18, 0), true))];

    private static readonly IsoDayOfWeek[] Four = [IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday];

    /// <summary>The household's contract: 23 long weeks (39 h), 22 short ones (37,75 h), from 2025-09-01.</summary>
    private static IReadOnlyList<WeekType> TwoTypes
        => [new("Longue", 23, Slots(Four)), new("Courte", 22, Slots(Four, new LocalTime(16, 45)))];

    private static async Task<(string Url, ItemId Id)> CreateWithAsync(NidApp app, decimal net, IReadOnlyList<WeekType> types, int day = 1)
    {
        var (created, failure) = await app.InvokeAsync(app.AdultA, new CreateChildcareCommand("Garde de Léa", app.Dependent, new CarerInput("Martine", null, null),
            new TermsInput(new LocalDate(2025, 9, day), net, null, 0.10m, 3.80m, 3.20m, types), [], Visibility.Shared, [new OwnerShare(app.AdultA, 10_000)]),
            TestContext.Current.CancellationToken);
        Assert.Null(failure);
        var id = (ItemId)created!;
        return ($"{Url}/{id.Value}", id);
    }

    private static async Task<Failure?> OverrideAsync(NidApp app, ItemId id, string monday, int weekType)
        => (await app.InvokeAsync(app.AdultA, new SetWeekOverrideCommand(id, LocalDate.FromDateTime(DateTime.Parse(monday, CultureInfo.InvariantCulture)), weekType),
            TestContext.Current.CancellationToken)).Failure;

    [Fact]
    public async Task Two_week_types_are_read_back_with_the_contract_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (url, _) = await CreateWithAsync(app, 4.40m, TwoTypes, day: 2); // not a Monday: the first year holds the Monday of the start week too, 53
        var detail = await GetAsync(app, url);
        var terms = detail.GetProperty("terms")[0];
        Assert.Equal("143.96", terms.GetProperty("monthlyHours").GetString());
        Assert.Equal("633.42", terms.GetProperty("monthlyBasePay").GetProperty("amount").GetString());
        Assert.Equal(45, terms.GetProperty("programmedWeeks").GetInt32());
        Assert.Equal(2, terms.GetProperty("weekTypes").GetArrayLength());
        Assert.Equal(53, detail.GetProperty("weeks").GetArrayLength());
        Assert.Equal("38.39", detail.GetProperty("summary").GetProperty("weeklyHours").GetString()); // 1 727,5 h over 45 weeks
        Assert.Equal(["Longue", "Courte"], detail.GetProperty("allocation").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public async Task An_override_is_set_then_removed_by_the_rotation()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (url, id) = await CreateWithAsync(app, 4.40m, TwoTypes);
        Assert.Null(await OverrideAsync(app, id, "2025-09-08", 0)); // the rotation gives Courte
        var row = (await GetAsync(app, url)).GetProperty("weeks").EnumerateArray().Single(w => w.GetProperty("monday").GetString() == "2025-09-08");
        Assert.True(row.GetProperty("overridden").GetBoolean());
        Assert.Equal(0, row.GetProperty("weekType").GetInt32());

        Assert.Null(await OverrideAsync(app, id, "2025-09-08", 1));
        row = (await GetAsync(app, url)).GetProperty("weeks").EnumerateArray().Single(w => w.GetProperty("monday").GetString() == "2025-09-08");
        Assert.False(row.GetProperty("overridden").GetBoolean());
        Assert.Equal(1, row.GetProperty("weekType").GetInt32());

        Assert.Equal(("weekType", ChildcareCodes.OverrideUnknownWeekType), Field(await OverrideAsync(app, id, "2025-09-08", 2)));
        Assert.Equal(("monday", ChildcareCodes.OverrideInvalidMonday), Field(await OverrideAsync(app, id, "2025-09-09", 0)));
    }

    [Fact]
    public async Task An_override_on_a_validated_week_is_refused_and_closing_drops_the_later_ones()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (url, id) = await CreateWithAsync(app, 4.40m, TwoTypes);
        Assert.Null(await OverrideAsync(app, id, "2025-09-08", 0));
        Assert.Null(await OverrideAsync(app, id, "2025-09-29", 1)); // the week runs into October
        await ValidateAsync(app, url, "2025-09");
        Assert.Equal(("monday", ChildcareCodes.StatementValidated), Field(await OverrideAsync(app, id, "2025-09-15", 0)));
        Assert.Equal(("monday", ChildcareCodes.StatementValidated), Field(await OverrideAsync(app, id, "2025-09-08", 1))); // removal too
        Assert.Equal(("monday", ChildcareCodes.StatementValidated), Field(await OverrideAsync(app, id, "2025-09-29", 0))); // Monday 09-29 is September
        Assert.Null(await OverrideAsync(app, id, "2025-10-06", 0));

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { on = "2025-09-30" })).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var left = await scope.ServiceProvider.GetRequiredService<IChildcareStore>().OverridesOfAsync([id], TestContext.Current.CancellationToken);
        Assert.Equal([new LocalDate(2025, 9, 8), new LocalDate(2025, 9, 29)], left.Select(o => o.Override.Monday));
    }

    private static (string Path, string Code) Field(Failure? failure) => (FieldErrors.Of(failure!)![0].Path, FieldErrors.Of(failure!)![0].Code);

    [Fact]
    public async Task An_override_replans_a_draft_day()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // Courte has no Friday: the week of 2025-09-08 is short, its Friday not planned.
        var (url, id) = await CreateWithAsync(app, 4m, [new("Longue", 23, Slots(Four)), new("Courte", 22, Slots([IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday]))]);
        var month = $"{url}/statements/2025-09";
        var read = await GetAsync(app, month);
        // A draft keeps its corrections only: the unplanned Friday of the short week is worked.
        var days = read.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString() == "2025-09-12"
            ? new { date = (string?)"2025-09-12", present = true, arrival = (string?)"08:00", departure = (string?)"17:00", lunch = true, absence = (string?)null }
            : new
            {
                date = d.GetProperty("date").GetString(), present = d.GetProperty("present").GetBoolean(), arrival = d.GetProperty("arrival").GetString(),
                departure = d.GetProperty("departure").GetString(), lunch = d.GetProperty("lunch").GetBoolean(), absence = d.GetProperty("absence").GetString(),
            }).ToList();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, month, new { days, mileage = "0", cmg = (string?)null, version = 0 })).StatusCode);
        Assert.Empty((await GetAsync(app, month)).GetProperty("problems").EnumerateArray());

        Assert.Null(await OverrideAsync(app, id, "2025-09-08", 0));
        var problems = (await GetAsync(app, month)).GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains(ChildcareCodes.DayReplanned, problems);
    }

    [Fact]
    public async Task A_revision_cannot_drop_a_week_type_an_override_points_at()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, id) = await CreateWithAsync(app, 4.40m, TwoTypes);
        Assert.Null(await OverrideAsync(app, id, "2025-10-13", 1)); // the rotation gives Longue; rank 1 is an exception
        var (_, failure) = await app.InvokeAsync(app.AdultA, new ReviseChildcareCommand(id,
            new TermsInput(new LocalDate(2025, 10, 1), 4.40m, null, 0.10m, 3.80m, 3.20m, [new("Semaine type", 46, Slots(Four))])), TestContext.Current.CancellationToken);
        Assert.Equal(ChildcareCodes.OverrideUnknownWeekType, Field(failure).Code);
    }

    [Fact]
    public async Task An_edit_cannot_drop_a_week_type_an_override_points_at()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (url, id) = await CreateWithAsync(app, 4.40m, TwoTypes);
        Assert.Null(await OverrideAsync(app, id, "2025-10-13", 1));
        var version = (await GetAsync(app, url)).GetProperty("version").GetUInt32();
        var (_, failure) = await app.InvokeAsync(app.AdultA, new EditChildcareCommand(id, "Garde de Léa", new CarerInput("Martine", null, null),
            new TermsInput(new LocalDate(2025, 9, 1), 4.40m, null, 0.10m, 3.80m, 3.20m, [new("Semaine type", 46, Slots(Four))]), version), TestContext.Current.CancellationToken);
        Assert.Equal(ChildcareCodes.OverrideUnknownWeekType, Field(failure).Code);
    }

    [Fact]
    public async Task An_override_of_an_earlier_revision_does_not_break_the_later_months()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (url, id) = await CreateWithAsync(app, 4.40m, TwoTypes);
        Assert.Null(await OverrideAsync(app, id, "2025-09-15", 1)); // rank 1 exists in the first revision only
        var (_, failure) = await app.InvokeAsync(app.AdultA, new ReviseChildcareCommand(id,
            new TermsInput(new LocalDate(2025, 10, 1), 4.40m, null, 0.10m, 3.80m, 3.20m, [new("Semaine type", 46, Slots(Four))])), TestContext.Current.CancellationToken);
        Assert.Null(failure);
        await GetAsync(app, $"{url}/statements/2025-10");
        await GetAsync(app, url);
    }

    [Fact]
    public async Task The_year_summary_gives_the_tax_estimate_s_figures()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await ProfileAsync(app);
        var arrangement = await CreateAsync(app);
        var september = await ValidateAsync(app, arrangement, "2025-09", cmg: "120.50");

        // Today is 2025-10-15: the arrangement has no 2024, so its own year is shown; October is not validated.
        var summary = await GetAsync(app, $"{arrangement}/summary");
        var tax = await GetAsync(app, "/api/v1/tax?year=2025&scope=household");
        Assert.Equal(2025, summary.GetProperty("year").GetInt32());
        Assert.Equal([2025], summary.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Equal("7GA", summary.GetProperty("box").GetString());
        Assert.Equal(TaxBase(september), summary.GetProperty("retained").GetProperty("amount").GetString());
        Assert.Equal(tax.GetProperty("inputs").GetProperty("childcareOutsideHome").GetString(), summary.GetProperty("retained").GetProperty("amount").GetString());
        var notice = tax.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("code").GetString() == "childcare_from_statements");
        Assert.Equal(summary.GetProperty("retained").GetProperty("amount").GetString(), notice.GetProperty("amount").GetString());
        Assert.Equal(summary.GetProperty("id").GetGuid(), notice.GetProperty("item").GetGuid());
        var declaration = september.GetProperty("figures").GetProperty("declaration");
        Assert.Equal(declaration.GetProperty("netPay").GetString(), summary.GetProperty("netPay").GetProperty("amount").GetString());
        Assert.Equal(declaration.GetProperty("upkeep").GetString(), summary.GetProperty("upkeep").GetProperty("amount").GetString());
        Assert.Equal(declaration.GetProperty("meals").GetString(), summary.GetProperty("meals").GetProperty("amount").GetString());
        Assert.Equal("120.50", summary.GetProperty("cmg").GetProperty("amount").GetString());
        Assert.Equal(["2025-09"], summary.GetProperty("validatedMonths").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(["2025-10"], summary.GetProperty("notValidated").EnumerateArray().Select(m => m.GetString()));

        // Another year sums nothing; a year out of range is refused.
        var empty = await GetAsync(app, $"{arrangement}/summary?year=2024");
        Assert.Equal("0.00", empty.GetProperty("retained").GetProperty("amount").GetString());
        Assert.Empty(empty.GetProperty("notValidated").EnumerateArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{arrangement}/summary?year=1800")).StatusCode);
    }
}
