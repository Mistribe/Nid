using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Platform.IntegrationTests;

/// <summary>
/// Issue #514: every catalogued kind, rendered over a fixed set of rows, reads word for word as <c>Snapshots/notification-copy.txt</c>.
/// A change of copy is a change of that file: set <c>NID_UPDATE_SNAPSHOTS=1</c> to rewrite it, then review the diff.
/// </summary>
public class NotificationCopyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly ItemId Item = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly Dictionary<string, string> Rich = new()
    {
        ["currency"] = "USD", ["note"] = "department_unknown", ["account"] = "Compte courant", ["reason"] = "room", ["name"] = "Courses",
        ["card"] = "Carte Visa", ["number"] = "1234", ["count"] = "2", ["min"] = "3", ["request"] = "GetEnvelope", ["admin"] = "1",
        ["job"] = "fx-daily", ["change"] = "cloud_on", ["worker"] = "GPU box", ["use"] = "chat", ["profile"] = "Cloud A", ["source"] = "fx",
        ["bank"] = "Banque A", ["hours"] = "48", ["month"] = "2027-02", ["category"] = "22222222-2222-2222-2222-222222222222",
        ["module"] = "childcare", ["net_worth"] = "up|investments.value+|loans-", ["commitments"] = "down|subscriptions-", ["income"] = "up",
        ["net_worth_amount"] = "302140.50", ["step_amount"] = "300000", ["balance_amount"] = "-120.4", ["charges"] = "Loyer",
        ["lines"] = "Supermarché", ["label"] = "Bail A",
    };

    private static readonly Dictionary<string, string> Alt = new()
    {
        ["currency"] = "EUR", ["reason"] = "too_old", ["hours"] = "26", ["change"] = "assistant_cloud_on", ["job"] = "ai.extract",
        ["source"] = "bank", ["admin"] = "0", ["module"] = "properties", ["use"] = "embed", ["count"] = "0", ["min"] = "1",
        ["recent_amount"] = "100", ["average_amount"] = "250.5", ["spent_amount"] = "900", ["usual_amount"] = "400",
    };

    private static IEnumerable<(string Case, NotificationRecord Row)> Rows(string kind)
    {
        NotificationRecord Row(IReadOnlyList<ItemId> items, LocalDate? dueOn, string key, Dictionary<string, string> p)
            => new(Guid.Empty, new PersonId(Guid.Empty), kind, key, NotificationLevel.Info, items, dueOn, new LocalDate(2027, 3, 1), p,
                NotificationStates.Open, null, null, Instant.FromUtc(2027, 3, 1, 0, 0), null, null);

        yield return ("item, due, month key", Row([Item], new LocalDate(2027, 3, 15), $"{kind}:{Item.Value:D}:2027-02", Rich));
        yield return ("item, day key", Row([Item], null, $"{kind}:{Item.Value:D}:2025-06-01", Alt));
        yield return ("item, bare", Row([Item], null, "k", []));
        yield return ("household, due", Row([], new LocalDate(2027, 5, 1), $"{kind}:2026-11-30", Rich));
        yield return ("household, alt", Row([], new LocalDate(2027, 1, 1), $"{kind}:proposal:33333333-3333-3333-3333-333333333333", Alt));
        yield return ("household, bare", Row([], null, "k", []));
    }

    [Fact]
    public async Task Every_kind_reads_as_its_snapshot()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<NotificationCatalog>();
        var names = new Dictionary<ItemId, string> { [Item] = "Maison" };

        var lines = new List<string>();
        foreach (var kind in catalog.Kinds.Values.OrderBy(k => k.Kind, StringComparer.Ordinal))
        {
            lines.Add($"{kind.Kind} — {NotificationTemplates.TitleOf(kind.Kind)}");
            foreach (var (name, row) in Rows(kind.Kind))
            foreach (var figures in new[] { false, true })
            {
                var r = NotificationTemplates.Render(row, kind, names, figures);
                lines.Add($"  {name}{(figures ? ", figures" : "")} | {r.Title} | {r.Body} | {r.Link}");
            }
        }

        Snapshot.Match("notification-copy.txt", lines);
    }
}
