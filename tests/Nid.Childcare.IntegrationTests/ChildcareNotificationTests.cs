using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Childcare.IntegrationTests;

/// <summary>Childcare decision 18: one signal per arrangement while the last month gone by is not validated (today: 2025-10-15).</summary>
public class ChildcareNotificationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> CreateAsync(NidApp app)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/childcare", new
        {
            name = "Garde de Léa", childId = app.Dependent.Value,
            carer = new { name = "Martine", pajemploi = (string?)null, approval = (string?)null },
            terms = new
            {
                from = "2025-09-01", netHourlyRate = "4.30", grossHourlyRate = (string?)null, overtimePct = "10", upkeepPer9h = "3.80", lunchPrice = (string?)null,
                weekTypes = new[] { new { name = "Semaine type", weeks = 46, schedule = new[] { new { day = 1, arrival = "08:00", departure = "17:30", lunch = false } } } },
            },
            gaps = Array.Empty<object>(), visibility = "shared",
            owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, LocalDate today)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "childcare");
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(app.AdultA);
        var result = await source.SignalsAsync(app.AdultA, today, today.PlusDays(30), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    [Fact]
    public async Task Statement_due_signals_the_last_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app);
        var signal = Assert.Single(await SignalsAsync(app, NidApp.GoldenCaseToday));
        Assert.Equal("childcare_statement_due", signal.Kind);
        Assert.Equal([new ItemId(id)], signal.ItemIds);
        Assert.Equal(new LocalDate(2025, 9, 30), signal.DueOn);
        Assert.Equal("2025-09", signal.Params["month"]);
    }

    [Fact]
    public async Task Validated_month_raises_nothing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var url = $"/api/v1/childcare/{await CreateAsync(app)}/statements/2025-09";
        var month = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, url)).RootElement;
        var days = month.GetProperty("days").EnumerateArray().Select(d => new
        {
            date = d.GetProperty("date").GetString(), present = d.GetProperty("present").GetBoolean(), arrival = d.GetProperty("arrival").GetString(),
            departure = d.GetProperty("departure").GetString(), lunch = d.GetProperty("lunch").GetBoolean(), absence = d.GetProperty("absence").GetString(),
        }).ToList();
        var saved = JsonDocument.Parse(await (await app.RawAsync(app.AdultA, HttpMethod.Put, url, new { days, mileage = "0", cmg = "0", version = 0 }))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/validate",
            new { version = saved.GetProperty("version").GetUInt32() })).StatusCode);
        Assert.Empty(await SignalsAsync(app, NidApp.GoldenCaseToday));
    }

    [Fact]
    public async Task Last_day_signals_the_current_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await CreateAsync(app);
        Assert.Equal("2025-10", Assert.Single(await SignalsAsync(app, new LocalDate(2025, 10, 31))).Params["month"]);
    }
}
