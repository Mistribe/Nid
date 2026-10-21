using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Investments.Infrastructure.Persistence.Migrations;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Investments;

public class EnvelopeStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    [Fact]
    public async Task An_imported_envelope_is_found_by_its_ref_per_importer_and_replaced_in_place()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var store = scope.ServiceProvider.GetRequiredService<IEnvelopeStore>();
        var item = ItemId.New();
        var adult = app.AdultA;
        var mark = new ImportMark("env-pea", new string('a', 64), adult);
        await store.CreateAsync(new Envelope(item, EnvelopeKind.Pea, "Banque", "1234", null, ValuationCadence.Monthly, null, null), mark, default);

        var found = Assert.Single((await store.FindImportedAsync(["env-pea"], default))["env-pea"]);
        Assert.Equal((item, mark.Hash, adult), (found.Item, found.Hash, found.ImportedBy));

        await store.ReplaceAsync(new Envelope(item, EnvelopeKind.Pea, null, null, null, ValuationCadence.Quarterly, 90, "n", new LocalDate(2025, 7, 31)),
            mark with { Hash = new string('b', 64) }, default);
        var stored = Assert.Single(await store.ReadAllAsync([item], default));
        Assert.Equal((null, ValuationCadence.Quarterly, 90, new LocalDate(2025, 7, 31), new string('b', 64)),
            (stored.Envelope.Institution, stored.Envelope.Cadence, stored.Envelope.StaleAfterDays, stored.Envelope.ClosedOn, stored.Import!.Hash));
    }

    [Fact]
    public async Task The_backfill_gives_an_already_closed_envelope_its_closing_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/close", new { closedOn = "2025-07-31" })).EnsureSuccessStatusCode();
        await using var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("update investments.envelope set closed_on = null");

        await db.Database.ExecuteSqlRawAsync(EnvelopeClosingAndImport.Backfill);

        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IEnvelopeStore>().FindAsync(id, default);
        Assert.Equal(new LocalDate(2025, 7, 31), stored!.ClosedOn);
    }
}
