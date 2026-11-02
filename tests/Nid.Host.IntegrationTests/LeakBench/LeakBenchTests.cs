using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Application;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Api;
using Nid.Platform;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Host.IntegrationTests.Admin;
using Nid.Host.IntegrationTests.Audit;
using Nid.Host.IntegrationTests.Mcp;
using Nid.TestSupport;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.IntegrationTests.LeakBench;

/// <summary>
/// The anti-leak bench of <c>docs/identity-and-access/access-tests.md</c>. Filtering is applicative, not
/// row-level (<c>architecture.md</c> D15), so the guarantee rests on these tests failing automatically when
/// a query or a command lets through an item it should not.
/// </summary>
public class LeakBenchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly string[] Scopes = ["household", "me"];

    /// <summary>
    /// The host's response serializer, mirrored from <c>Program.cs</c>'s <c>ConfigureHttpJsonOptions</c>:
    /// the textual leak scan below is only worth anything if it reads what the wire would carry.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new ItemIdJsonConverter(), new LocalDateJsonConverter() },
    };

    [Fact]
    public void Every_request_in_every_module_is_covered_by_the_bench()
    {
        var uncovered = RequestCatalogue.Discover().Except(RequestCatalogue.Covered).ToList();

        Assert.True(uncovered.Count == 0,
            "These requests have no leak-bench entry. Add them to RequestCatalogue.Entries:\n  "
            + string.Join("\n  ", uncovered.Select(t => t.FullName)));
    }

    /// <summary>
    /// Discovery is only as good as what it sees. Reading the generated register groups back proves the
    /// bench is looking at what the application actually wires up, and that no module declares a request
    /// nobody handles (access-tests.md §3.1).
    /// </summary>
    [Fact]
    public void Every_request_a_module_declares_is_registered_with_Synapse()
    {
        Assert.NotEmpty(RequestCatalogue.Registered);

        var unregistered = RequestCatalogue.Declared.Except(RequestCatalogue.Registered).ToList();

        Assert.True(unregistered.Count == 0,
            "These requests are declared but no handler is registered for them, so the bench cannot run them:\n  "
            + string.Join("\n  ", unregistered.Select(t => t.FullName)));
    }

    /// <summary>
    /// <c>IsItemScoped</c> is written by hand in the catalogue, and it decides whether a request goes through
    /// the 7x2 access matrix at all. Checked in BOTH directions: a command that carries an item but is
    /// catalogued <c>false</c> by copy-paste would otherwise leave the matrix with nothing failing.
    /// </summary>
    [Fact]
    public void Every_catalogue_entry_declares_its_item_scope_truthfully()
    {
        foreach (var entry in RequestCatalogue.Entries)
        {
            // A proposal's submission may have no target; with one, it is checked as any item-scoped request.
            var implements = typeof(IItemScoped).IsAssignableFrom(entry.RequestType) || typeof(IOptionalItemScoped).IsAssignableFrom(entry.RequestType);

            Assert.True(implements == entry.IsItemScoped,
                $"{entry.RequestType.Name}: the catalogue says IsItemScoped={entry.IsItemScoped}, but the type "
                + $"{(implements ? "does" : "does not")} implement IItemScoped. A mismatch takes the request out "
                + "of the per-item access matrix without anything failing.");
        }
    }

    /// <summary>
    /// Discovery must follow the composition root, not a transcription of it. This only proves the
    /// derivation finds what is there today; what makes a future module join is that the derivation walks
    /// <c>Nid.Host</c>'s assembly closure, which <c>AddNid</c> cannot wire up without.
    /// </summary>
    [Fact]
    public void Discovery_is_derived_from_the_composition_root()
    {
        Assert.Contains(typeof(ListPersonsQuery).Assembly, RequestCatalogue.ModuleAssemblies);
        Assert.Contains(typeof(ListSubscriptionsQuery).Assembly, RequestCatalogue.ModuleAssemblies);

        var groups = RequestCatalogue.RegisterGroups.Select(g => g.GetType()).ToList();
        Assert.Contains(typeof(Nid.Host.RegisterGroup), groups);
        Assert.Contains(typeof(Nid.Platform.RegisterGroup), groups);
        Assert.Contains(typeof(Nid.Core.Application.RegisterGroup), groups);
        Assert.Contains(typeof(Nid.Subscriptions.Application.RegisterGroup), groups);
    }

    /// <summary>
    /// access-tests.md §6: a read whose response carries items must implement <see cref="IExposesItems"/>,
    /// or the output guard has nothing to inspect and the leak goes out unnoticed. Commands are out of
    /// scope: they answer with the id of the item they just acted on, which the item-access behavior has
    /// already checked the actor against, and that id is the actor's own doing rather than a disclosure.
    /// </summary>
    [Fact]
    public void Every_read_that_carries_items_exposes_them_to_the_output_guard()
    {
        var blind = new List<string>();

        foreach (var request in RequestCatalogue.Discover().Where(t => !RequestCatalogue.IsCommand(t)))
        {
            var response = RequestCatalogue.ResponseTypeOf(request);
            if (response is null || typeof(IExposesItems).IsAssignableFrom(response))
                continue;

            if (CarriesItemIds(response, []))
                blind.Add($"{request.FullName} -> {response.FullName}");
        }

        Assert.True(blind.Count == 0,
            "These responses carry ItemIds without implementing IExposesItems, so the output guard cannot "
            + "check them:\n  " + string.Join("\n  ", blind));
    }

    /// <summary>
    /// A proposal view carries its <c>documentId</c> to the endpoints, which hide it from a non-reader (<c>ProposalEndpoints.HideDocumentsAsync</c>,
    /// for REST and MCP alike): masked, just not by the pipeline. Exempt as a leaf, so anything else a proposal response holds is still walked.
    /// </summary>
    private static readonly Type MaskedAtTheEndpoint = typeof(Nid.Core.Application.ProposalView);

    /// <summary>
    /// Review Focus 2 of #108: a response that holds a document id, directly or in a list, implements <see cref="IExposesDocuments"/>, or the
    /// document mask has nothing to null and the id of a document the reader cannot read goes out.
    /// </summary>
    [Fact]
    public void Every_response_carrying_a_document_id_masks_it()
    {
        var unmasked = RequestCatalogue.Discover()
            .Select(RequestCatalogue.ResponseTypeOf).OfType<Type>().Distinct()
            .Where(r => !typeof(IExposesDocuments).IsAssignableFrom(r) && CarriesDocumentIds(r, []))
            .Select(r => r.FullName).ToList();

        Assert.True(unmasked.Count == 0,
            "These responses carry a DocumentId without implementing IExposesDocuments, so the document mask cannot null it:\n  "
            + string.Join("\n  ", unmasked));
    }

    /// <summary>The walk of <see cref="CarriesItemIds"/>, looking for a record with a <c>Guid? DocumentId</c>.</summary>
    private static bool CarriesDocumentIds(Type type, HashSet<Type> seen)
    {
        if (type == MaskedAtTheEndpoint || !seen.Add(type))
            return false;

        IEnumerable<Type> elements = type.IsArray
            ? [type.GetElementType()!]
            : type.GetInterfaces().Concat([type])
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(i => i.GetGenericArguments()[0]);
        if (elements.Concat(type.IsGenericType ? type.GetGenericArguments() : []).Any(e => CarriesDocumentIds(e, seen)))
            return true;

        if (type.Assembly.GetName().Name?.StartsWith("Nid.", StringComparison.Ordinal) != true)
            return false;

        var properties = type.GetProperties().Where(p => p.GetIndexParameters().Length == 0).ToList();
        return properties.Any(p => p.Name == "DocumentId" && p.PropertyType == typeof(Guid?))
            || properties.Any(p => CarriesDocumentIds(p.PropertyType, seen));
    }

    [Fact]
    public void The_document_walk_sees_a_view_in_a_list()
    {
        Assert.True(CarriesDocumentIds(typeof(Nid.Investments.Application.EnvelopeDetail), []));
        Assert.True(CarriesDocumentIds(typeof(Nid.RealEstate.Application.PropertyDetail), []));
        Assert.False(CarriesDocumentIds(typeof(Nid.Investments.Application.EnvelopeList), []));
    }

    /// <summary>The details of a shared row's twins, whose values and flows cite their owner's private document (#108).</summary>
    private static string[] StampedDetails(MixedDataset data, string shared)
        => [$"/api/v1/investments/envelopes/{data.Envelopes[shared].Value}", $"/api/v1/realestate/properties/{data.Properties[shared].Value}"];

    /// <summary>Any module's list, read for its ids only.</summary>
    private sealed record IdListResponse(IReadOnlyList<IdListRow> Items);

    private sealed record IdListRow(Guid Id);

    /// <summary>
    /// The bench's second axis, and the reason it exists: <see cref="RequestCatalogue.Entries"/> builds each
    /// request naming its target and nothing else, which is exactly why the matrix above reported full
    /// coverage while a purchase could grant to a subscription the actor may not write. A request whose
    /// payload can reach another <see cref="ItemId"/> must therefore also declare
    /// <see cref="CatalogueEntry.BuildNamingAnother"/> — checked here by reflection, so the next request
    /// that carries one joins the axis without anyone remembering to.
    /// </summary>
    [Fact]
    public void Every_item_scoped_request_that_can_name_a_second_item_is_on_the_second_axis()
    {
        var missing = new List<string>();

        foreach (var entry in RequestCatalogue.Entries.Where(e => e.IsItemScoped && e.BuildNamingAnother is null))
        {
            // Everything but the route's own ItemId: that one the pipeline already checks.
            var carrying = entry.RequestType.GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0
                            && p.Name != nameof(IItemScoped.ItemId)
                            && CarriesItemIds(p.PropertyType, []))
                .Select(p => p.Name)
                .ToList();

            if (carrying.Count > 0)
                missing.Add($"{entry.RequestType.FullName} (through {string.Join(", ", carrying)})");
        }

        Assert.True(missing.Count == 0,
            "These item-scoped requests can name a second item in their payload but have no "
            + "BuildNamingAnother entry, so nothing checks the actor against that second item:\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The fact export pages are not vacuous in the bench: every readable envelope twin and property twin exports its value and
    /// its flow (3F1 spec §6: the property twins' estimate and expense), every readable property twin its planned cost and its
    /// tenancy, and every readable loan twin its link; the other adult's private twins export none of it.
    /// </summary>
    [Fact]
    public async Task The_fact_export_pages_carry_the_twins_facts_to_their_readers_only()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var export = new Nid.Host.IntegrationTests.Export.ExportClient(app);

        foreach (var (actor, own, other) in new[]
                 { (app.AdultA, MixedDataset.PrivateLabelA, MixedDataset.PrivateLabelB), (app.AdultB, MixedDataset.PrivateLabelB, MixedDataset.PrivateLabelA) })
        {
            var all = await export.ExportAllAsync(actor);
            var ids = all["envelopes"].ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
            var properties = all["properties"].ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("id").GetString()!);
            Assert.Equal(6, ids.Count);
            Assert.Equal(6, properties.Count);
            Assert.Contains(own, ids.Keys);
            Assert.Contains(own, properties.Keys);
            Assert.DoesNotContain(other, ids.Keys);
            Assert.DoesNotContain(other, properties.Keys);
            foreach (var section in new[] { "valuations", "flows" })
            {
                // The reader's own private income source adds its one-off income to the flows (the other adult's stays hidden).
                var oneOff = section == "flows" ? [all["income_sources"].Single(e => e.GetProperty("name").GetString() == own).GetProperty("id").GetString()!] : Array.Empty<string>();
                Assert.Equal(12 + oneOff.Length, all[section].Count);
                Assert.Equal(ids.Values.Concat(properties.Values).Concat(oneOff).Order(StringComparer.Ordinal),
                    all[section].Select(f => f.GetProperty("item").GetString()!).Order(StringComparer.Ordinal));
                Assert.DoesNotContain(other, JsonSerializer.Serialize(all[section]));
            }
            foreach (var section in new[] { "property_recurring_costs", "tenancies" })
            {
                Assert.Equal(6, all[section].Count);
                Assert.Equal(properties.Values.Order(StringComparer.Ordinal),
                    all[section].Select(r => r.GetProperty("property").GetString()!).Order(StringComparer.Ordinal));
                Assert.DoesNotContain(other, JsonSerializer.Serialize(all[section]));
            }

            // The loan twins: each linked to a property twin the reader exports (a private one to its owner's shared one).
            Assert.Equal(6, all["loans"].Count);
            Assert.All(all["loans"], l => Assert.Contains(l.GetProperty("property").GetString()!, properties.Values));
            Assert.DoesNotContain(other, JsonSerializer.Serialize(all["loans"]));
        }
    }

    /// <summary>
    /// Tax assessments spec §6: B's solo 2018 notice and its private PDF reach B alone. A's reads of the notices, the documents and the
    /// export carry neither its figure nor its ids; B's own read does (positive control).
    /// </summary>
    [Fact]
    public async Task A_solo_tax_notice_and_its_pdf_reach_their_declarant_only()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var (notice, document) = data.NoticeB;
        await using var mcp = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultA, TokenPresets.Read)).Secret);
        await using var mcpB = await McpHarness.ClientAsync(app, (await TokenApi.CreateAsync(app, app.AdultB, TokenPresets.Read)).Secret);

        async Task<string[]> ReadsAsync(PersonId actor, Func<string, Task<string>> tool)
            => [
                await app.GetRawAsync(actor, "/api/v1/tax/assessments?scope=household"),
                await app.GetRawAsync(actor, "/api/v1/tax/assessments?scope=me"),
                await app.GetRawAsync(actor, "/api/v1/documents?scope=household"),
                await app.GetRawAsync(actor, "/api/v1/documents?scope=me&archived=true"),
                await tool("list_tax_years"),
                await tool("list_documents"),
                JsonSerializer.Serialize(await new Nid.Host.IntegrationTests.Export.ExportClient(app).ExportAllAsync(actor)),
            ];
        async Task<string> Call(ModelContextProtocol.Client.McpClient client, string name)
            => McpHarness.TextOf(await client.CallToolAsync(name, name == "list_tax_years" ? new Dictionary<string, object?> { ["scope"] = "me" } : new Dictionary<string, object?>()));

        foreach (var body in await ReadsAsync(app.AdultA, t => Call(mcp, t)))
        {
            Assert.DoesNotContain("987654", body, StringComparison.Ordinal);
            Assert.DoesNotContain(notice.ToString(), body, StringComparison.Ordinal);
            Assert.DoesNotContain(document.ToString(), body, StringComparison.Ordinal);
        }

        var own = await app.GetRawAsync(app.AdultB, "/api/v1/tax/assessments?scope=me");
        Assert.Contains("987654", own, StringComparison.Ordinal);
        Assert.Contains(notice.ToString(), own, StringComparison.Ordinal);
        Assert.Contains("987654", await Call(mcpB, "list_tax_years"), StringComparison.Ordinal);
        Assert.Contains(document.ToString(), await app.GetRawAsync(app.AdultB, "/api/v1/documents?scope=me"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec §7: a row's document is edited and archived by its uploader and by whoever writes the row; one who only reads the row is
    /// forbidden, and anyone else meets « not found ». Every <see cref="CatalogueEntry.OnEveryRow"/> command, on every row, as A and as B.
    /// </summary>
    [Fact]
    public async Task Every_document_write_is_refused_exactly_as_the_rules_say()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var commands = RequestCatalogue.Entries.Where(e => e.OnEveryRow && RequestCatalogue.IsCommand(e.RequestType)).ToList();
        Assert.Equal(2, commands.Count);

        foreach (var entry in commands)
        foreach (var (name, item) in data.Items)
        foreach (var actor in new[] { app.AdultA, app.AdultB })
        {
            var (_, failure) = await app.InvokeAsync(actor, entry.Build(data, item));
            var uploader = data.UploaderOf(item) == actor;
            var where = $"{entry.RequestType.Name} on {name}'s document by {data.NameOf(actor)}";

            if (!uploader && !data.ExpectedReadable(actor).Contains(item))
                Assert.True(failure is NotFoundFailure, $"{where}: expected not found, got {Describe(failure)}");
            else if (!uploader && !data.ExpectedWritable(name, actor))
                Assert.True(failure is UnauthorizedFailure, $"{where}: expected forbidden, got {Describe(failure)}");
            else
                Assert.True(failure is null, $"{where}: expected success, got {Describe(failure)}");
        }
    }

    /// <summary>
    /// Spec §7: S-A's document is linked to S-A and to A's private P-A, S-B's to S-B and P-B. The other adult reads it through the shared
    /// link and sees neither the private link's label nor its id; and, editing neither the document nor the shared item, cannot link it to
    /// an item of their own.
    /// </summary>
    [Fact]
    public async Task A_shared_document_shows_no_private_link_and_its_reader_cannot_link_it()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        foreach (var (shared, hidden, owner, reader, own) in new[]
                 {
                     ("S-A", "P-A", app.AdultA, app.AdultB, new[] { "S-B", "P-B" }),
                     ("S-B", "P-B", app.AdultB, app.AdultA, new[] { "S-A", "P-A" }),
                 })
        {
            var document = data.Documents[data[shared]];
            var label = data.ForbiddenLabel(reader);
            var url = $"/api/v1/documents/{document}";

            Assert.Contains(label, await app.GetRawAsync(owner, url), StringComparison.Ordinal); // positive control
            foreach (var body in new[] { await app.GetRawAsync(reader, url), await app.GetRawAsync(reader, "/api/v1/documents?scope=me") })
            {
                Assert.DoesNotContain(label, body, StringComparison.Ordinal);
                Assert.DoesNotContain(data[hidden].Value.ToString(), body, StringComparison.Ordinal);
            }

            foreach (var target in own)
            {
                var (_, failure) = await app.InvokeAsync(reader, new Nid.Documents.Application.LinkDocumentCommand(document,
                    new Nid.Documents.Contracts.DocumentTarget(Nid.Documents.Contracts.TargetKinds.CoreItem, data[target].Value), "statement"));
                Assert.True(failure is UnauthorizedFailure, $"{data.NameOf(reader)} linking {shared}'s document to {target}: got {Describe(failure)}");
            }

            // Nothing linked: read from the store, as no adult sees every link.
            await using var scope = app.Services.CreateAsyncScope();
            var links = await scope.ServiceProvider.GetRequiredService<Nid.Documents.Application.IDocumentStore>().LinksOfAsync([document], default);
            Assert.Equal(new[] { data[shared].Value, data[hidden].Value }.Order(), links.Select(l => l.TargetId).Order());
        }
    }

    /// <summary>
    /// 3E2: the routine proposes exactly the property twins visible in « Moi » that the actor writes — each one's estimate, and
    /// each one's rent (every twin's tenancy covers September 2025).
    /// </summary>
    [Fact]
    public async Task The_property_routine_lists_exactly_the_property_twins_the_actor_may_write()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        foreach (var actor in new[] { app.AdultA, app.AdultB })
        {
            var visible = data.ExpectedVisible(actor, VisibilityScope.Me, data.Properties);
            var expected = data.Properties.Where(kv => visible.Contains(kv.Value) && data.ExpectedWritable(kv.Key, actor))
                .Select(kv => data.NameOf(kv.Value)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var routine = JsonDocument.Parse(await app.GetRawAsync(actor, "/api/v1/realestate/routine")).RootElement;
            foreach (var list in new[] { "estimates", "rents" })
                Assert.Equal(expected, routine.GetProperty(list).EnumerateArray()
                    .Select(l => data.NameOf(new ItemId(l.GetProperty("property").GetGuid()))).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// 3E2: the routine records on a property twin exactly when the actor writes it (then undoes it, so the next actor meets no
    /// estimate at that date); any other twin answers « not found » and gets nothing — each twin is written by A or B, whose
    /// routine then still reads the seeded estimate as the latest.
    /// </summary>
    [Fact]
    public async Task The_property_routine_records_only_on_a_property_twin_the_actor_may_write()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        foreach (var actor in new[] { app.AdultA, app.AdultB })
        foreach (var (name, property) in data.Properties)
        {
            var response = await app.RawAsync(actor, HttpMethod.Post, "/api/v1/realestate/routine",
                new { on = "2025-09-30", estimates = new[] { new { property = property.Value, amount = "1000" } } });
            var writable = data.ExpectedWritable(name, actor);
            Assert.True(response.StatusCode == (writable ? HttpStatusCode.OK : HttpStatusCode.NotFound),
                $"{name} recorded by {data.NameOf(actor)}: expected writable={writable}, got {response.StatusCode}");
            if (!writable)
                continue;
            var valuation = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("lines")[0].GetProperty("valuationId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(actor, HttpMethod.Post, "/api/v1/realestate/routine/undo",
                new { facts = new[] { new { property = property.Value, valuationId = valuation } } })).StatusCode);
        }

        foreach (var actor in new[] { app.AdultA, app.AdultB })
            Assert.All(JsonDocument.Parse(await app.GetRawAsync(actor, "/api/v1/realestate/routine")).RootElement.GetProperty("estimates").EnumerateArray(),
                e => Assert.Equal("2025-03-31", e.GetProperty("lastAsOf").GetString()));
    }

    [Fact]
    public async Task Both_adults_write_an_item_owned_by_the_dependent()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        foreach (var actor in new[] { app.AdultA, app.AdultB })
            (await app.RawAsync(actor, HttpMethod.Patch, $"/api/v1/subscriptions/{data["D"].Value}",
                new { note = "adults write a dependent's item" })).EnsureSuccessStatusCode();
    }

    /// <summary>access-tests.md §4: the household totals depend on no private item.</summary>
    [Fact]
    public async Task The_household_totals_never_depend_on_a_private_item()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        // Give A's private item a cost big enough that a total counting it could not be mistaken.
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{data["P-A"].Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = "99.99", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        const string url = "/api/v1/subscriptions?scope=household&from=2025-01-01&to=2025-10-01";
        var forA = await app.GetAsync<SubscriptionApi.ListResponse>(app.AdultA, url);
        var forB = await app.GetAsync<SubscriptionApi.ListResponse>(app.AdultB, url);

        Assert.DoesNotContain(forA.Items, i => i.Id == data["P-A"].Value);
        Assert.Equal(forB.HouseholdCashCost, forA.HouseholdCashCost);
    }

    /// <summary>
    /// access-tests.md §5 — the test that protects the mechanism itself: "sans lui, la garde pourrait être
    /// désactivée par erreur sans que rien n'échoue". Every other test in this file stays green when
    /// <c>[assembly: SynapseGlobalBehavior(typeof(OutputGuardBehavior&lt;,&gt;))]</c> is deleted from
    /// <c>Program.cs</c>, because nothing else reaches a state the guard is the only thing standing in.
    ///
    /// The probe is deliberately NOT a request of its own. Synapse's generator enumerates the global
    /// behaviors per (request, response) pair at compile time — see the generated
    /// <c>Nid.Host.RegisterGroup</c> — so a handler registered at run time from this assembly would never
    /// receive the guard and the test would prove nothing. A real, registered request is made to leak
    /// instead: <see cref="IVisibleItems"/> is replaced by one that answers generously in the Household
    /// scope, the scope the handler asks for, and truthfully in Me, the scope the guard asks for. The
    /// handler then builds a list holding the other adult's private item, and only the guard can stop it.
    /// </summary>
    [Fact]
    public async Task A_response_that_escapes_the_visible_set_is_refused_with_no_data_and_recorded()
    {
        var violations = new RecordingViolations();
        await using var app = await NidApp.StartAsync(fixture, services =>
        {
            services.AddSingleton<IAccessViolations>(violations);
            services.AddScoped<IVisibleItems, GenerousInTheHouseholdScope>();
        });

        var privateOfA = await app.NewSubscriptionAsync(MixedDataset.PrivateLabelA, app.AdultA, Visibility.Private);

        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, "/api/v1/subscriptions?scope=household");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(MixedDataset.PrivateLabelA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(privateOfA.Value.ToString(), body, StringComparison.Ordinal);
        Assert.Contains(nameof(ListSubscriptionsQuery), violations.Recorded);
    }

    /// <summary>
    /// access-tests.md §3.5: every admin-only request is refused to B, who is not admin; run by A, who is, its
    /// response names no item — the role opens nothing, not even A's own labels.
    /// </summary>
    [Fact]
    public async Task Every_admin_only_request_is_refused_to_the_non_admin_and_shows_the_admin_no_label()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var adminOnly = RequestCatalogue.Entries.Where(e => typeof(IAdminOnly).IsAssignableFrom(e.RequestType)).ToList();
        Assert.NotEmpty(adminOnly);

        foreach (var entry in adminOnly)
        {
            var (_, refused) = await app.InvokeAsync(app.AdultB, entry.Build(data, data["S-A"]));
            Assert.True(refused?.Code == AdminCodes.Required,
                $"{entry.RequestType.Name} as B (not admin): expected {AdminCodes.Required}, got {refused?.Code ?? "success"}");
        }

        foreach (var entry in adminOnly.Where(e => !RequestCatalogue.IsCommand(e.RequestType)))
        {
            var (response, failure) = await app.InvokeAsync(app.AdultA, entry.Build(data, data["S-A"]));
            // The bench runs without a scheduler: a job's detail is not found by design (admin jobs design §3.4); B is still refused above.
            if (JobsOffNotFound.Contains(entry.RequestType) && failure?.Code == "NOT_FOUND")
                continue;
            Assert.True(failure is null, $"{entry.RequestType.Name} as A (admin) failed with {failure?.Code}");

            var serialised = JsonSerializer.Serialize(response, ResponseJson);
            foreach (var label in new[] { MixedDataset.PrivateLabelA, MixedDataset.PrivateLabelB, "shared-a", "joint-ab" })
                Assert.DoesNotContain(label, serialised, StringComparison.Ordinal);
        }
    }

    private static readonly HashSet<Type> JobsOffNotFound = [typeof(Nid.Platform.Admin.GetJobQuery), typeof(Nid.Platform.Admin.GetJobRunQuery)];

    /// <summary>Truthful where the guard looks (Me), generous where the handler looks (Household).</summary>
    private sealed class GenerousInTheHouseholdScope(CoreDbContext db) : IVisibleItems
    {
        Task<IReadOnlySet<ItemId>> IVisibleItems.ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
            => ForAsync(actor, scope, includeArchived: false, ct);

        public async Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, bool includeArchived, CancellationToken ct)
        {
            if (scope != VisibilityScope.Household)
                return await new VisibleItems(db).ForAsync(actor, scope, includeArchived, ct);

            var everything = await db.Items.AsNoTracking().Select(i => i.Id).ToListAsync(ct);
            return everything.Select(id => new ItemId(id)).ToHashSet();
        }
    }

    private sealed class RecordingViolations : IAccessViolations
    {
        public List<string> Recorded { get; } = [];

        public ValueTask RecordAsync(string request, PersonId actor, IReadOnlyCollection<ItemId> leaked, CancellationToken ct)
        {
            Recorded.Add(request);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Every_request_that_names_a_provider_is_on_the_provider_axis()
    {
        var missing = RequestCatalogue.Entries
            .Where(e => e.BuildLinkingProvider is null && e.RequestType.GetProperty("ProviderKey") is not null)
            .Select(e => e.RequestType.FullName)
            .ToList();

        Assert.True(missing.Count == 0,
            "These requests carry a ProviderKey but have no BuildLinkingProvider entry, so nothing checks that a "
            + "provider only a hidden item uses cannot be linked:\n  " + string.Join("\n  ", missing));
    }

    private static string Describe(Failure? failure)
        => failure is null ? "success" : $"{failure.GetType().Name}({failure.Code})";

    /// <summary>
    /// What a failure means to a caller, ignoring which item or row it happened to name: a fixed identifier
    /// baked into a typed failure (a purchase id freshly seeded for each check, say) would otherwise make two
    /// occurrences of the exact same refusal compare unequal.
    /// </summary>
    private static string Signature(Failure? failure) => failure switch
    {
        null => "success",
        _ when FieldErrors.Of(failure) is { } fields
            => $"{failure.Code}:{string.Join(",", fields.Select(e => $"{e.Path}={e.Code}"))}",
        _ => failure.Code,
    };

    /// <summary>
    /// Whether a type carries <see cref="ItemId"/> anywhere a caller can reach.
    ///
    /// A nested <see cref="IExposesItems"/> does NOT stop the walk, on purpose: the output guard inspects the
    /// top-level response only, so a <c>Page&lt;T&gt;</c> that does not implement the interface while its
    /// <c>T</c> does is precisely the shape that would sail past both the guard and this test. Only the root
    /// is exempt, and its caller checks that before calling in.
    ///
    /// Generic arguments are walked whatever the assembly: a dictionary's values reach us through
    /// <c>KeyValuePair&lt;,&gt;</c>, which is not one of our types, and a nullable through
    /// <c>Nullable&lt;&gt;</c>. Properties are only walked on our own types — an ItemId cannot hide in a
    /// framework type's own members without one of ours holding it first.
    /// </summary>
    private static bool CarriesItemIds(Type type, HashSet<Type> seen)
    {
        if (type == typeof(ItemId))
            return true;
        if (!seen.Add(type))
            return false;

        IEnumerable<Type> elements = type.IsArray
            ? [type.GetElementType()!]
            : type.GetInterfaces().Concat([type])
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(i => i.GetGenericArguments()[0]);

        if (elements.Any(e => CarriesItemIds(e, seen)))
            return true;

        if (type.IsGenericType && type.GetGenericArguments().Any(a => CarriesItemIds(a, seen)))
            return true;

        if (type.Assembly.GetName().Name?.StartsWith("Nid.", StringComparison.Ordinal) != true)
            return false;

        return type.GetProperties()
            .Any(p => p.GetIndexParameters().Length == 0 && CarriesItemIds(p.PropertyType, seen));
    }

    // Response shapes the walk above has to see through. None of them is a real response: they exist so the
    // test below fails when the walk loses its reach, which is the only way the guard's automatic coverage
    // can quietly shrink.
    private sealed record PlainRow(ItemId Id, string Label);

    private sealed record ExposingRow(ItemId Id) : IExposesItems
    {
        public IEnumerable<ItemId> ExposedItems() => [Id];
    }

    private sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);

    private sealed record ByKey(IReadOnlyDictionary<string, PlainRow> Rows);

    private sealed record ByExposingKey(IReadOnlyDictionary<string, ExposingRow> Rows);

    private sealed record Maybe(ItemId? Id);

    private sealed record Harmless(string Name, int Count);

    /// <summary>
    /// 1C2b spec §3.6: every item of the dataset uses an adopted provider of its own. A first sync leaves them all
    /// adopted (remaining); a second, whose catalog holds every one of them, replaces them all. A, the admin, reads the
    /// journal: a remaining key appears exactly when A may read the item using it, and a replaced key is listed as
    /// replaced exactly then — otherwise as added, as if never adopted, since the catalog itself is public. The table
    /// of §2, not the code under test, says which. The view has no count to leak what it dropped.
    /// </summary>
    [Fact]
    public async Task The_sync_report_names_no_provider_only_a_hidden_item_uses()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;
        var keys = new Dictionary<string, string>();
        foreach (var (name, item) in data.Items)
        {
            var key = "adopted-" + name.ToLowerInvariant();
            await app.SeedProviderAsync(key, key, ProviderOrigin.Adopted);
            await app.LinkProviderAsync(item, key);
            keys[name] = key;
        }
        var labels = Encoding.UTF8.GetBytes("""[{"key":"streaming","name":"Streaming","color":"#FF6400F6"}]""");
        var catalog = "[" + string.Join(",", keys.Values.Select(k => $$"""{"key":"{{k}}","name":"{{k}}","categories":["streaming"]}""")) + "]";
        foreach (var providers in new[] { "[]", catalog })
        {
            app.Catalog.Serve(labels, Encoding.UTF8.GetBytes(providers));
            var (_, syncFailure) = await app.InvokeAsync(app.AdultA, new SyncCatalogCommand());
            Assert.Null(syncFailure);
        }

        var (answer, failure) = await app.InvokeAsync(app.AdultA, new GetCatalogRunsQuery());

        Assert.Null(failure);
        var runs = ((CatalogRunsView)answer!).Runs;
        var (replacing, leaving) = (runs[0].Report!, runs[1].Report!);
        var leavingSerialised = JsonSerializer.Serialize(runs[1], ResponseJson);
        foreach (var (name, key) in keys)
        {
            var readable = data.ExpectedReadable(app.AdultA).Contains(data[name]);
            Assert.True(readable == leavingSerialised.Contains($"\"{key}\"", StringComparison.Ordinal),
                $"{key} (used by {name}) in A's first run: expected {(readable ? "listed" : "absent")}");
            Assert.True(readable == replacing.AdoptedReplaced.Contains(key),
                $"{key} (used by {name}) in A's replaced keys: expected {(readable ? "listed" : "absent")}");
            Assert.True(readable != replacing.ProvidersAdded.Contains(key),
                $"{key} (used by {name}) in A's added keys: expected {(readable ? "absent" : "listed")}");
        }
        Assert.Empty(replacing.Refused);
        Assert.Empty(replacing.AdoptedRemaining);
        Assert.All(leaving.AdoptedRemaining, key => Assert.Contains(key, keys.Values));
    }

    public static TheoryData<bool, Type> ReachShapes => new()
    {
        { true, typeof(PlainRow) },
        // The hole this closes: the guard looks at the top-level response only, so a page whose rows expose
        // items while the page itself does not is unchecked at run time AND unnoticed by the architecture test.
        { true, typeof(Page<ExposingRow>) },
        { true, typeof(Page<PlainRow>) },
        // A dictionary reaches its values through KeyValuePair<,>, which is not one of our types.
        { true, typeof(ByKey) },
        { true, typeof(ByExposingKey) },
        { true, typeof(Maybe) },
        { true, typeof(IReadOnlyList<ExposingRow>) },
        { false, typeof(Harmless) },
        { false, typeof(IReadOnlyList<string>) },
        { false, typeof(IReadOnlyDictionary<string, string>) },
    };

    /// <summary>
    /// The reach of <see cref="CarriesItemIds"/> is what makes
    /// <see cref="Every_read_that_carries_items_exposes_them_to_the_output_guard"/> worth anything: a walk
    /// that stops too early reports a clean bill of health for a response the guard cannot see into.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReachShapes))]
    public void The_walk_that_finds_unguarded_responses_sees_through_nesting(bool expected, Type shape)
        => Assert.True(expected == CarriesItemIds(shape, []),
            $"{shape} : expected CarriesItemIds={expected}. A response of this shape "
            + (expected
                ? "carries ItemIds the output guard would never be handed."
                : "carries none, and flagging it would train the next author to ignore this test."));

    /// <summary>4D1 D9: a private item's proposals reach its owner only, and the other adult's refused read is recorded.</summary>
    [Fact]
    public async Task A_private_items_proposals_reach_its_owner_only_and_a_refused_read_is_recorded()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        var mine = await app.GetRawAsync(app.AdultB, "/api/v1/core/proposals");
        Assert.All(data.Proposals["P-B"], id => Assert.Contains(id.ToString(), mine)); // positive control
        var theirs = await app.GetRawAsync(app.AdultA, "/api/v1/core/proposals");
        Assert.All(data.Proposals["P-B"], id => Assert.DoesNotContain(id.ToString(), theirs));
        Assert.DoesNotContain(MixedDataset.PrivateLabelB, theirs, StringComparison.Ordinal);

        foreach (var id in data.Proposals["P-B"])
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"/api/v1/core/proposals/{id}")).StatusCode);
        var denied = await AuditApi.EventsAsync(app, SecurityEventNames.AccessDenied);
        foreach (var target in new[] { data.Envelopes["P-B"], data.IncomeSources["P-B"], data["P-B"], data.Loans["P-B"] })
            Assert.Contains(denied, e => e.PersonId == app.AdultA.Value && e.ItemIds.Contains(target.Value));

        // A's decisions on B's proposals: 404, still pending, and a refusal recorded for the target (one per minute, so by `read`).
        foreach (var id in data.Proposals["P-B"])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/core/proposals/{id}/approve", new { version = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/core/proposals/{id}/reject", new { version = 0 })).StatusCode);
            var detail = JsonDocument.Parse(await app.GetRawAsync(app.AdultB, $"/api/v1/core/proposals/{id}")).RootElement;
            Assert.Equal("pending", detail.GetProperty("proposal").GetProperty("status").GetString());
        }
        denied = await AuditApi.EventsAsync(app, SecurityEventNames.AccessDenied);
        foreach (var target in new[] { data.Envelopes["P-B"], data.IncomeSources["P-B"], data["P-B"], data.Loans["P-B"] })
            Assert.Contains(denied, e => e.PersonId == app.AdultA.Value && e.ItemIds.Contains(target.Value) && e.Data.Contains("\"read\""));
    }

    // The slowest walks, split into nested test classes that run alongside this one: xunit runs the tests of one
    // class one after another, and this class alone was the tail of the run.
    /// <summary>What a read answers each actor.</summary>
    public class Reads(PostgresFixture fixture) : IClassFixture<PostgresFixture>
    {
        [Fact]
        public async Task No_read_leaks_the_label_of_the_other_adults_private_item()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            foreach (var actor in new[] { app.AdultA, app.AdultB })
            foreach (var url in new[]
                     {
                         "/api/v1/subscriptions?scope=household",
                         "/api/v1/subscriptions?scope=me",
                         "/api/v1/investments/envelopes?scope=household",
                         "/api/v1/investments/envelopes?scope=me",
                         "/api/v1/investments/routine",
                         "/api/v1/income/sources?scope=household",
                         "/api/v1/income/sources?scope=me",
                         "/api/v1/realestate/loans?scope=household",
                         "/api/v1/realestate/loans?scope=me",
                         "/api/v1/realestate/properties?scope=household",
                         "/api/v1/realestate/properties?scope=me",
                         "/api/v1/realestate/routine",
                         "/api/v1/dashboard?scope=household",
                         "/api/v1/dashboard?scope=me",
                         "/api/v1/dashboard/net-worth?scope=household&period=all",
                         "/api/v1/dashboard/net-worth?scope=me&period=all",
                         "/api/v1/projection?scope=household",
                         "/api/v1/projection?scope=me",
                         "/api/v1/core/persons",
                         "/api/v1/goals?scope=household",
                         "/api/v1/goals?scope=me",
                         "/api/v1/documents?scope=household",
                         "/api/v1/documents?scope=me&archived=true",
                         "/api/v1/documents?inbox=true",
                     }.Concat(data.PrivatePhases.Values.Select(p => $"/api/v1/documents?targetKind=income.phase&targetId={p}"))
                         .Concat(data.PrivateOneOffs.Values.Select(o => $"/api/v1/documents?targetKind=income.one_off&targetId={o}"))
                         .Concat(data.PrivatePhases.Keys.Select(n => $"/api/v1/documents?anchor={data.IncomeSources[n].Value}")).Concat(data.Goals.Values.Where(g => data.ExpectedReadable(actor).Contains(g))
                         .SelectMany(g => new[] { $"/api/v1/goals/{g.Value}?scope=household", $"/api/v1/goals/{g.Value}?scope=me" }))
                         .Concat(StampedDetails(data, "S-A")).Concat(StampedDetails(data, "S-B")))
            {
                var body = await app.GetRawAsync(actor, url);
                Assert.DoesNotContain(data.ForbiddenLabel(actor), body, StringComparison.Ordinal);
                Assert.DoesNotContain(data.ForbiddenDocument(actor).ToString(), body, StringComparison.Ordinal);
            }

            // Positive control: each adult reads her own private document's id on her stamped shared twins, so the scan above reads a cited source.
            foreach (var (shared, owner, other) in new[] { ("S-A", app.AdultA, app.AdultB), ("S-B", app.AdultB, app.AdultA) })
            foreach (var url in StampedDetails(data, shared))
                Assert.Contains(data.ForbiddenDocument(other).ToString(), await app.GetRawAsync(owner, url), StringComparison.Ordinal);

            // Positive control: each owner's by-target read finds her phase's document, so the scan above reads a filled list.
            foreach (var (hidden, owner, label) in new[] { ("P-A", app.AdultA, MixedDataset.PrivateLabelA), ("P-B", app.AdultB, MixedDataset.PrivateLabelB) })
                Assert.Contains(label, await app.GetRawAsync(owner, $"/api/v1/documents?targetKind=income.phase&targetId={data.PrivatePhases[hidden]}"),
                    StringComparison.Ordinal);
            foreach (var (hidden, owner, label) in new[] { ("P-A", app.AdultA, MixedDataset.PrivateLabelA), ("P-B", app.AdultB, MixedDataset.PrivateLabelB) })
                Assert.Contains(label, await app.GetRawAsync(owner, $"/api/v1/documents?targetKind=income.one_off&targetId={data.PrivateOneOffs[hidden]}"),
                    StringComparison.Ordinal);
            foreach (var (hidden, owner, label) in new[] { ("P-A", app.AdultA, MixedDataset.PrivateLabelA), ("P-B", app.AdultB, MixedDataset.PrivateLabelB) })
                Assert.Contains(label, await app.GetRawAsync(owner, $"/api/v1/documents?anchor={data.IncomeSources[hidden].Value}"), StringComparison.Ordinal);
        }

        [Fact]
        public async Task No_read_returns_an_item_outside_the_actors_visible_set()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            foreach (var actor in new[] { app.AdultA, app.AdultB })
            foreach (var scope in Scopes)
            {
                var list = await app.GetAsync<SubscriptionApi.ListResponse>(actor, $"/api/v1/subscriptions?scope={scope}");
                var allowed = data.ExpectedReadable(actor);

                foreach (var item in list.Items)
                    Assert.True(allowed.Contains(new ItemId(item.Id)),
                        $"{scope} as {data.NameOf(actor)} returned {data.NameOf(new ItemId(item.Id))}, which the rules hide");

                // Equality, not containment: this also catches an item that wrongly disappears.
                var expected = data.ExpectedVisible(actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household);
                Assert.Equal(
                    expected.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    list.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));

                // The same, exactly, for the envelope twins (2B1a): the table expects of them what it expects of their rows.
                var envelopes = await app.GetAsync<IdListResponse>(actor, $"/api/v1/investments/envelopes?scope={scope}");
                var expectedEnvelopes = data.ExpectedVisible(
                    actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household, data.Envelopes);
                Assert.Equal(
                    expectedEnvelopes.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    envelopes.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));

                // And for the income twins (2C1).
                var sources = await app.GetAsync<IdListResponse>(actor, $"/api/v1/income/sources?scope={scope}");
                var expectedSources = data.ExpectedVisible(
                    actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household, data.IncomeSources);
                Assert.Equal(
                    expectedSources.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    sources.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));

                // And for the loan twins (3A2): none has ended, so the default list holds them all.
                var loans = await app.GetAsync<IdListResponse>(actor, $"/api/v1/realestate/loans?scope={scope}");
                var expectedLoans = data.ExpectedVisible(
                    actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household, data.Loans);
                Assert.Equal(
                    expectedLoans.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    loans.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));

                // And for the property twins (3C1).
                var properties = await app.GetAsync<IdListResponse>(actor, $"/api/v1/realestate/properties?scope={scope}");
                var expectedProperties = data.ExpectedVisible(
                    actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household, data.Properties);
                Assert.Equal(
                    expectedProperties.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    properties.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));

                // And for the goal twins.
                var goals = await app.GetAsync<IdListResponse>(actor, $"/api/v1/goals?scope={scope}");
                var expectedGoals = data.ExpectedVisible(actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household, data.Goals);
                Assert.Equal(
                    expectedGoals.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    goals.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));

                // And the dashboard (2D2a, 3E1): its split lists exactly the visible open envelopes, properties and loans (a private
                // loan only in « me »), and every item named in « À venir » or « À faire » is one this actor may read.
                var dashboard = System.Text.Json.JsonDocument.Parse(await app.GetRawAsync(actor, $"/api/v1/dashboard?scope={scope}")).RootElement;
                Assert.Equal(
                    expectedEnvelopes.Concat(expectedProperties).Concat(expectedLoans).Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                    dashboard.GetProperty("envelopeSplit").EnumerateArray()
                        .Select(e => data.NameOf(new ItemId(e.GetProperty("id").GetGuid()))).OrderBy(n => n, StringComparer.Ordinal));
                foreach (var section in new[] { "upcoming", "todo" })
                foreach (var entry in dashboard.GetProperty(section).EnumerateArray())
                    Assert.Contains(new ItemId(entry.GetProperty("id").GetGuid()), data.ExpectedReadable(actor));

                // 3E1: each visible property twin's tax and tenancy end are in « À venir », and no other property's.
                foreach (var kind in new[] { "property_cost_due", "tenancy_end" })
                    Assert.Equal(
                        expectedProperties.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                        dashboard.GetProperty("upcoming").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == kind)
                            .Select(e => data.NameOf(new ItemId(e.GetProperty("id").GetGuid()))).OrderBy(n => n, StringComparer.Ordinal));
            }
        }
    }

    /// <summary>Every catalogued request, run as each adult against the rules.</summary>
    public class Catalogue(PostgresFixture fixture) : IClassFixture<PostgresFixture>
    {
        /// <summary>
        /// access-tests.md §3.2: every read of the catalogue, run as A and as B. No item outside the actor's
        /// set of §2 may appear in what the handler returns.
        /// </summary>
        [Fact]
        public async Task No_catalogued_read_returns_an_item_or_a_label_the_actor_may_not_see()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            // Every serialised read, per actor: what the textual scan below looks through.
            var bodies = new Dictionary<PersonId, List<string>>
            {
                [app.AdultA] = [],
                [app.AdultB] = [],
            };
            var envelopeBodies = new Dictionary<PersonId, List<string>>
            {
                [app.AdultA] = [],
                [app.AdultB] = [],
            };
            var incomeBodies = new Dictionary<PersonId, List<string>>
            {
                [app.AdultA] = [],
                [app.AdultB] = [],
            };
            var loanBodies = new Dictionary<PersonId, List<string>>
            {
                [app.AdultA] = [],
                [app.AdultB] = [],
            };
            var propertyBodies = new Dictionary<PersonId, List<string>>
            {
                [app.AdultA] = [],
                [app.AdultB] = [],
            };
            var documentBodies = new Dictionary<PersonId, List<string>>
            {
                [app.AdultA] = [],
                [app.AdultB] = [],
            };

            foreach (var entry in RequestCatalogue.Entries)
            {
                // The catalogue is the contract a new request's author fills in: check it builds what it claims
                // before anything downstream relies on it.
                var rows = data.ItemsFor(entry.RequestType);
                var sample = entry.Build(data, rows["S-A"]);
                Assert.IsType(entry.RequestType, sample);

                if (RequestCatalogue.IsCommand(entry.RequestType))
                    continue;

                List<ItemId> targets = entry.IsItemScoped || entry.OnEveryRow ? [.. rows.Values] : [rows["S-A"]];

                foreach (var actor in new[] { app.AdultA, app.AdultB })
                foreach (var target in targets)
                {
                    var (response, failure) = await app.InvokeAsync(actor, entry.Build(data, target));

                    // A row's document is read by its uploader and by whoever reads the row; anyone else meets « not found ».
                    if (entry.OnEveryRow)
                        Assert.True(
                            data.ExpectedReadable(actor).Contains(target) || data.UploaderOf(target) == actor ? failure is null : failure is NotFoundFailure,
                            $"{entry.RequestType.Name} on {data.NameOf(target)}'s document as {data.NameOf(actor)}: got {Describe(failure)}");

                    if (response is null)
                        continue;

                    // Identifiers: nothing outside the actor's set of §2.
                    if (response is IExposesItems exposing)
                    {
                        var allowed = data.ExpectedReadable(actor);
                        foreach (var exposed in exposing.ExposedItems())
                            Assert.True(allowed.Contains(exposed),
                                $"{entry.RequestType.Name} as {data.NameOf(actor)} exposed {data.NameOf(exposed)}, "
                                + "which the rules hide");
                    }

                    // #108: the document mask casts what the response masks into back to the response's type.
                    if (response is IExposesDocuments documents)
                        Assert.IsType(response.GetType(), documents.WithReadableDocuments(new HashSet<Guid>()));

                    // Document ids carry no label for the textual scan: each must be one the actor reads, by its uploader or its row.
                    if (entry.RequestType == typeof(Nid.Documents.Application.FilterReadableDocumentsQuery))
                        foreach (var (row, document) in data.Documents.Concat(data.UnlinkDocuments))
                            Assert.True(((IReadOnlySet<Guid>)response).Contains(document) == (data.ExpectedReadable(actor).Contains(row) || data.UploaderOf(row) == actor),
                                $"FilterReadableDocumentsQuery as {data.NameOf(actor)} on {data.NameOf(row)}'s document");

                    // A download holds a stream, which does not serialise: its bytes are checked, and what it names is scanned.
                    if (response is Nid.Documents.Application.DocumentContent content)
                        await using (content.Content)
                        {
                            using var bytes = new MemoryStream();
                            await content.Content.CopyToAsync(bytes);
                            Assert.Equal("application/pdf", content.MediaType);
                            Assert.Equal(DocumentApi.Pdf.Length, bytes.Length);
                            response = new { content.MediaType, content.FileName, bytes.Length };
                        }

                    // Text: not just the ids. A finding message or a catalogue join naming another item would
                    // carry a label the identifier check cannot see. Every read gets this for free.
                    var serialised = JsonSerializer.Serialize(response, ResponseJson);
                    bodies[actor].Add(serialised);
                    if (entry.RequestType.Assembly == typeof(Nid.Documents.Application.ListDocumentsQuery).Assembly)
                        documentBodies[actor].Add(serialised);
                    if (rows == data.Envelopes)
                        envelopeBodies[actor].Add(serialised);
                    if (rows == data.IncomeSources)
                        incomeBodies[actor].Add(serialised);
                    if (rows == data.Loans)
                        loanBodies[actor].Add(serialised);
                    if (rows == data.Properties)
                        propertyBodies[actor].Add(serialised);

                    Assert.DoesNotContain(data.ForbiddenLabel(actor), serialised, StringComparison.Ordinal);
                    Assert.DoesNotContain(data.ForbiddenDocument(actor).ToString(), serialised, StringComparison.Ordinal);
                }
            }

            // Positive control: without it, a rename or a [JsonIgnore] would empty the scan above and nothing
            // would fail. "shared-a" is a label BOTH adults are entitled to read.
            // The envelope and income reads get their own: the subscriptions' bodies alone would satisfy the one above.
            foreach (var actor in new[] { app.AdultA, app.AdultB })
            {
                Assert.Contains(bodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
                Assert.Contains(envelopeBodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
                Assert.Contains(incomeBodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
                Assert.Contains(loanBodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
                Assert.Contains(propertyBodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
                Assert.Contains(documentBodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
                // 3C2: a tenancy labelled and a recurring cost noted with the row's label reach the property reads, so the scan above looks through them.
                Assert.Contains(propertyBodies[actor], body => body.Contains("\"label\":\"shared-a\"", StringComparison.Ordinal)
                    && body.Contains("\"note\":\"shared-a\",\"monthlyEquivalent\"", StringComparison.Ordinal));
            }
            // Bank sync: A's account reaches A's banking reads, so the scan above looks through them (and B's never carry it).
            Assert.Contains(bodies[app.AdultA], body => body.Contains(MixedDataset.PrivateLabelA + " compte", StringComparison.Ordinal));
            Assert.Contains(bodies[app.AdultA], body => body.Contains(MixedDataset.PrivateLabelA + " ligne 1", StringComparison.Ordinal));
            // D19: B reads the joint line A linked to her private subscription, A reads the link's name (lines, rules).
            Assert.Contains(bodies[app.AdultB], body => body.Contains("joint-ab ligne", StringComparison.Ordinal));
            Assert.Contains(bodies[app.AdultA], body => body.Contains($"\"itemName\":\"{MixedDataset.PrivateLabelA}\"", StringComparison.Ordinal));
        }

        /// <summary>
        /// access-tests.md §3.3: every item-scoped request, run on every item of the dataset by every actor,
        /// through the real pipeline. The expected outcome is read off the table of §2, never computed by the
        /// code under test.
        /// </summary>
        [Fact]
        public async Task Every_catalogued_item_scoped_request_is_refused_exactly_as_the_rules_say()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            foreach (var entry in RequestCatalogue.Entries.Where(e => e.IsItemScoped))
            foreach (var (name, item) in data.ItemsFor(entry.RequestType))
            foreach (var actor in new[] { app.AdultA, app.AdultB })
            {
                var request = entry.Build(data, item);
                var access = request switch
                {
                    IItemScoped scoped => scoped.Access,
                    IOptionalItemScoped { ItemId: not null } optional => optional.Access,
                    _ => throw new Xunit.Sdk.XunitException($"{entry.RequestType.Name} is catalogued item-scoped but carries no item."),
                };
                var where = $"{entry.RequestType.Name} on {name} by {data.NameOf(actor)}";

                var (_, failure) = await app.InvokeAsync(actor, request);

                if (!data.ExpectedReadable(actor).Contains(item))
                {
                    Assert.True(failure is NotFoundFailure { Resource: "item" },
                        $"{where}: the rules hide this item, so it must be 'not found' — existence is never "
                        + $"disclosed — but the pipeline answered {Describe(failure)}");
                    continue;
                }

                if (access == AccessKind.Write && !data.ExpectedWritable(name, actor))
                {
                    Assert.True(failure is UnauthorizedFailure,
                        $"{where}: the rules let this actor read but not write, so it must be forbidden, "
                        + $"but the pipeline answered {Describe(failure)}");
                    continue;
                }

                // Allowed. The request may still fail on its own merits — the bench invents phase and purchase
                // ids — but it must never be turned away by the access rules.
                Assert.False(failure is UnauthorizedFailure,
                    $"{where}: refused although the rules allow it — {Describe(failure)}");
                Assert.False(failure is NotFoundFailure { Resource: "item" },
                    $"{where}: hidden although the rules allow it — {Describe(failure)}");
            }
        }

        [Fact]
        public async Task Every_item_scoped_request_matches_what_the_rules_say()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            foreach (var (name, item) in data.Items)
            foreach (var actor in new[] { app.AdultA, app.AdultB })
            {
                var readable = data.ExpectedReadable(actor).Contains(item);
                var response = await app.RawAsync(actor, HttpMethod.Get, $"/api/v1/subscriptions/{item.Value}");

                Assert.True(readable == (response.StatusCode == HttpStatusCode.OK),
                    $"{name} read by {data.NameOf(actor)}: expected readable={readable}, got {response.StatusCode}");

                if (!readable)
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // existence is never disclosed
            }
        }
    }

    /// <summary>What a write may touch.</summary>
    public class Writes(PostgresFixture fixture) : IClassFixture<PostgresFixture>
    {
        /// <summary>
        /// The provider axis: every write that links a provider, as A and as B, naming a provider only <c>other</c> uses.
        /// The target's own refusal comes first, as on the item axes; then the link succeeds exactly when the actor may
        /// read <c>other</c>, and is otherwise the same <c>subscriptions.unknown_provider</c> as a key that does not exist.
        /// </summary>
        [Fact]
        public async Task No_write_links_a_provider_only_a_hidden_item_makes_visible()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            foreach (var entry in RequestCatalogue.Entries.Where(e => e.BuildLinkingProvider is not null))
            {
                List<(string Name, ItemId Id)> targets = entry.IsItemScoped ? [.. data.Items.Select(i => (i.Key, i.Value))] : [("new", default)];
                foreach (var (targetName, target) in targets)
                foreach (var (otherName, other) in data.Items)
                foreach (var actor in new[] { app.AdultA, app.AdultB })
                {
                    var key = await app.SeedCustomProviderUsedByAsync(other); // fresh each time: nothing else makes it visible
                    var where = $"{entry.RequestType.Name} on {targetName} linking {otherName}'s provider by {data.NameOf(actor)}";

                    var (_, failure) = await app.InvokeAsync(actor, entry.BuildLinkingProvider!(data, actor, target, key));

                    if (entry.IsItemScoped && !data.ExpectedReadable(actor).Contains(target))
                    {
                        Assert.True(failure is NotFoundFailure { Resource: "item" }, $"{where}: expected 'not found', got {Describe(failure)}");
                        continue;
                    }
                    if (entry.IsItemScoped && !data.ExpectedWritable(targetName, actor))
                    {
                        Assert.True(failure is UnauthorizedFailure, $"{where}: expected forbidden, got {Describe(failure)}");
                        continue;
                    }

                    if (data.ExpectedReadable(actor).Contains(other))
                        Assert.True(failure is null, $"{where}: the actor can see {otherName}, yet the link was refused — {Describe(failure)}");
                    else
                        Assert.True(failure?.Code == CatalogCodes.UnknownProvider,
                            $"{where}: only a hidden item uses this provider, so it must be unknown — got {Describe(failure)}");
                }
            }
        }

        /// <summary>
        /// Each second-axis request, aimed at every item of the dataset while naming every other one, as A and
        /// as B. A write reaching two items needs the right to write both: the target's refusal is the usual one
        /// (§6: hidden is not found, readable-but-not-writable is forbidden), while a second item the actor may
        /// not write is always <em>not found</em> — it was never disclosed to them in the first place.
        /// </summary>
        [Fact]
        public async Task No_request_touches_a_second_item_the_actor_may_not_write()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            foreach (var entry in RequestCatalogue.Entries.Where(e => e.BuildNamingAnother is not null))
            foreach (var (targetName, target) in data.ItemsFor(entry.RequestType))
            foreach (var (otherName, other) in data.ItemsFor(entry.RequestType))
            {
                if (target == other)
                    continue;

                foreach (var actor in new[] { app.AdultA, app.AdultB })
                {
                    var request = await entry.BuildNamingAnother!(data, target, other);
                    var where = $"{entry.RequestType.Name} on {targetName} naming {otherName} by {data.NameOf(actor)}";

                    var (_, failure) = await app.InvokeAsync(actor, request);

                    if (!data.ExpectedReadable(actor).Contains(target))
                    {
                        Assert.True(failure is NotFoundFailure { Resource: "item" },
                            $"{where}: the rules hide the target, so it must be 'not found' — but the pipeline "
                            + $"answered {Describe(failure)}");
                        continue;
                    }

                    if (!data.ExpectedWritable(targetName, actor))
                    {
                        Assert.True(failure is UnauthorizedFailure,
                            $"{where}: the rules let this actor read but not write the target, so it must be "
                            + $"forbidden, but the pipeline answered {Describe(failure)}");
                        continue;
                    }

                    // A link needs only the second item's read access (spec 3C1 §3); every other second item, its write access.
                    if (entry.SecondItemAccess == AccessKind.Read
                            ? !data.ExpectedReadable(actor).Contains(other)
                            : !data.ExpectedWritable(otherName, actor))
                    {
                        // 1E2b1 design §3.2: a purchase's refused grant is now a located 422, not a typed
                        // NotFoundFailure — but §6 still requires it to read exactly like a target that does not
                        // exist at all, so the ruling here is indistinguishability, checked against that case.
                        var missing = await entry.BuildNamingAnother!(data, target, new ItemId(Guid.NewGuid()));
                        var (_, missingFailure) = await app.InvokeAsync(actor, missing);
                        Assert.Equal(Signature(missingFailure), Signature(failure));
                        continue;
                    }

                    // The rules never turn this away: it must succeed.
                    Assert.True(failure is null,
                        $"{where}: the rules let this actor write both items, yet it was refused — {Describe(failure)}");
                }
            }
        }

        [Fact]
        public async Task A_write_on_an_item_the_actor_may_only_read_is_forbidden_not_silently_applied()
        {
            var data = await MixedDataset.CreateAsync(fixture);
            await using var app = data.App;

            // S-A is shared: B may read it, and per AccessRules only its owners write it.
            var response = await app.RawAsync(app.AdultB, HttpMethod.Patch,
                $"/api/v1/subscriptions/{data["S-A"].Value}", new { autoRenew = false });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Refused, and nothing was written on the way out: the value B tried to set is still what A left.
            var detail = await app.GetRawAsync(app.AdultA, $"/api/v1/subscriptions/{data["S-A"].Value}");
            Assert.Contains("\"autoRenew\":true", detail, StringComparison.Ordinal);
        }
    }
}
