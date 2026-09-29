using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Investments;

[Collection("postgres")]
public class EnvelopeStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_envelope_round_trips_and_a_change_touches_only_what_it_names()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        // No Synapse handler runs here (none exists yet, Task 4 adds it), so nothing sets the ambient actor the
        // audit interceptor requires (1F3a U4) the way ActorBehavior would in the real pipeline.
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IEnvelopeStore>();
        var item = ItemId.New();
        var envelope = new Envelope(item, EnvelopeKind.Pea, "Banque", "4521", new LocalDate(2020, 3, 1),
            ValuationCadence.Monthly, null, "note");

        await store.CreateAsync(envelope, default);
        Assert.Equal(envelope, await store.FindAsync(item, default));

        await store.UpdateAsync(item, new EnvelopeChange(Cadence: ValuationCadence.Quarterly, ClearAccountHint: true), default);
        Assert.Equal(envelope with { Cadence = ValuationCadence.Quarterly, AccountHint = null }, await store.FindAsync(item, default));
        Assert.Null(await store.FindAsync(ItemId.New(), default));
        Assert.Single(await store.FindManyAsync([item, ItemId.New()], default));
    }
}
