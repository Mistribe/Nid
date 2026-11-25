using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Application.Dashboard;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence.Migrations;
using Nid.Platform.Notifications;
using Nid.TestSupport;

namespace Nid.Host.IntegrationTests.Host;

/// <summary>
/// Issue #514: a notification or event kind is named <c>&lt;module&gt;.&lt;kind&gt;</c> in snake case, a notification kind's prefix being
/// the module it declares. #645 renamed the last unprefixed ones with their stored rows.
/// </summary>
public partial class KindNamingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [GeneratedRegex("^[a-z]+(_[a-z]+)*\\.[a-z0-9]+(_[a-z0-9]+)*$")]
    private static partial Regex Prefixed();

    [Fact]
    public async Task Every_kind_is_prefixed_by_its_module()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationCatalog>().Kinds.Values;
        var events = app.Services.GetRequiredService<UpcomingCatalog>().Kinds.Keys;

        Assert.All(notifications,
            k => Assert.True(Prefixed().IsMatch(k.Kind) && k.Kind.StartsWith(k.Module + ".", StringComparison.Ordinal), $"{k.Kind} ({k.Module})"));
        Assert.All(events, k => Assert.Matches(Prefixed(), k));
        // #645: every renamed row lands on a declared kind, and no legacy name is declared any more.
        var declared = notifications.Select(k => k.Kind).Concat(events).ToHashSet();
        Assert.All(PrefixedNotificationKinds645.Renames, r => Assert.True(declared.Contains(r.New) && !declared.Contains(r.Old), $"{r.Old} → {r.New}"));
    }

    /// <summary>#644: a kind published through <c>INotifier</c> is declared by the module it belongs to, as a source's kinds are.</summary>
    [Fact]
    public async Task Every_published_kind_is_declared_by_its_module()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var published = app.Services.GetServices<IPublishedNotificationKinds>().ToList();

        Assert.Equal(["ai", "core", "platform"], published.Select(Owner).Order());
        Assert.All(published, p => Assert.All(p.Kinds, k => Assert.Equal(Owner(p), k.Module)));
    }

    private static string Owner(IPublishedNotificationKinds kinds) => kinds.GetType().Namespace!.Split('.')[1].ToLowerInvariant();
}
