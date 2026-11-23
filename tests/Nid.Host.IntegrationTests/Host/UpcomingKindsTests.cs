using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Calendar;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// Issue #514: every event kind of « À venir » the modules declare, with its cadence and its words in the calendar feed over a fixed set of
/// labels, reads as <c>Snapshots/upcoming-kinds.txt</c>. <c>NID_UPDATE_SNAPSHOTS=1</c> rewrites it.
/// </summary>
public class UpcomingKindsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string?[] Labels =
    [
        null, "Phase 2", "household", "department_unknown", "life_insurance_8y", "pea_5y", "per_retirement",
        "works", "construction_works", "property_tax", "condo_fees", "insurance", "energy", "maintenance", "management_fees", "other", "unknown",
    ];

    [Fact]
    public async Task Every_kind_reads_as_its_snapshot()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var kinds = app.Services.GetRequiredService<UpcomingCatalog>();

        var lines = new List<string>();
        foreach (var (kind, declared) in kinds.Kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            lines.Add($"{kind} — {declared.Cadence}, milestone: {kinds.IsMilestone(kind)}");
            foreach (var label in Labels)
            {
                var item = new DashboardEventView(new LocalDate(2027, 3, 1), kind, new ItemId(Guid.Empty), "subscriptions", "Maison", label, null);
                var household = item with { Id = null, Name = "Alice" };
                lines.Add($"  {label ?? "(none)"} | {CalendarIcs.Summary(item, kinds)} | {CalendarIcs.Summary(household, kinds)}");
            }
        }

        Snapshot.Match("upcoming-kinds.txt", lines);
    }
}
