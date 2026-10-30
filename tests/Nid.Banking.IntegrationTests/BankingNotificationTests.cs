using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §5: the banking source tells a connection's creator, and only them, that a consent ends or a sync keeps failing (today: 2025-10-15).</summary>
public class BankingNotificationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly Instant Now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();

    private static async Task<Guid> ConnectionAsync(NidApp app, PersonId person, Duration validFor)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var id = await store.CreatePendingAsync(person, "Boursorama", "FR", SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())), Now + Duration.FromMinutes(15), default);
        await store.ActivateAsync(id, "session", Now + validFor, default);
        return id;
    }

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(NidApp app, PersonId reader)
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Notifier(reader);
        var source = scope.ServiceProvider.GetServices<INotificationSource>().Single(s => s.Name == "banking");
        var today = NidApp.GoldenCaseToday;
        var result = await source.SignalsAsync(reader, today, today.PlusDays(90), CancellationToken.None);
        Assert.True(result.TryGetValue(out var signals), result.TryGetFailure(out var f) ? f!.Message : "");
        return signals!;
    }

    [Fact]
    public async Task A_consent_ending_in_ten_days_tells_its_creator_only()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(10));

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal("banking.consent_expiring", signal.Kind);
        Assert.Equal($"banking.consent_expiring:{id:D}:2025-10-25:14", signal.Key);
        Assert.Empty(signal.ItemIds);
        Assert.Equal(new KeyValuePair<string, string>("bank", "Boursorama"), Assert.Single(signal.Params));
        Assert.Empty(await SignalsAsync(app, app.AdultB));
    }

    [Fact]
    public async Task A_consent_far_from_its_end_says_nothing()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        await ConnectionAsync(app, app.AdultA, Duration.FromDays(60));

        Assert.Empty(await SignalsAsync(app, app.AdultA));
    }

    [Fact]
    public async Task An_expired_consent_is_told()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(10));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().SetStatusAsync(id, ConnectionStatus.Expired, null, default);
        }

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal($"banking.consent_expired:{id:D}:2025-10-25", signal.Key);
    }

    [Fact]
    public async Task A_consent_revoked_by_the_sync_is_told_as_expired_to_its_creator_only()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(60));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().SetStatusAsync(id, ConnectionStatus.Revoked, null, default);
        }

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));

        Assert.Equal($"banking.consent_expired:{id:D}:2025-12-14", signal.Key);
        Assert.Empty(await SignalsAsync(app, app.AdultB));
    }

    [Fact]
    public async Task Two_failing_nights_tell_a_failing_sync_and_one_does_not()
    {
        await using var app = await BankingApi.StartAsync(fixture);
        var id = await ConnectionAsync(app, app.AdultA, Duration.FromDays(60));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        await store.RecordSyncAsync(id, Now, failed: true, default);
        Assert.Empty(await SignalsAsync(app, app.AdultA));
        await store.RecordSyncAsync(id, Now, failed: true, default);

        var signal = Assert.Single(await SignalsAsync(app, app.AdultA));
        Assert.Equal("banking.sync_failing", signal.Kind);
        Assert.StartsWith($"banking.sync_failing:{id:D}:", signal.Key);
    }

    [Fact]
    public async Task Banking_turned_off_gives_no_signal()
    {
        await using var app = await BankingApi.StartAsync(fixture, enable: false);
        await ConnectionAsync(app, app.AdultA, Duration.FromDays(10));

        Assert.Empty(await SignalsAsync(app, app.AdultA));
    }
}
