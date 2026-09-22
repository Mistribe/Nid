using System.Net;
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
using Nid.TestSupport;
using UnambitiousFx.Functional.Failures;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

/// <summary>
/// The anti-leak bench of <c>docs/identity-and-access/access-tests.md</c>. Filtering is applicative, not
/// row-level (<c>architecture.md</c> D15), so the guarantee rests on these tests failing automatically when
/// a query or a command lets through an item it should not.
/// </summary>
[Collection("postgres")]
public class LeakBenchTests(PostgresFixture fixture)
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
            var implements = typeof(IItemScoped).IsAssignableFrom(entry.RequestType);

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

        foreach (var request in RequestCatalogue.Discover().Where(t => !typeof(ICommandMarker).IsAssignableFrom(t)))
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

    [Fact]
    public async Task No_read_returns_an_item_outside_the_actors_visible_set()
    {
        var data = await MixedDataset.CreateAsync(fixture);
        await using var app = data.App;

        foreach (var actor in new[] { app.AdultA, app.AdultB })
        foreach (var scope in Scopes)
        {
            var list = await app.GetAsync<ReadEndpointTests.ListResponse>(actor, $"/api/v1/subscriptions?scope={scope}");
            var allowed = data.ExpectedReadable(actor);

            foreach (var item in list.Items)
                Assert.True(allowed.Contains(new ItemId(item.Id)),
                    $"{scope} as {data.NameOf(actor)} returned {data.NameOf(new ItemId(item.Id))}, which the rules hide");

            // Equality, not containment: this also catches an item that wrongly disappears.
            var expected = data.ExpectedVisible(actor, scope == "me" ? VisibilityScope.Me : VisibilityScope.Household);
            Assert.Equal(
                expected.Select(i => data.NameOf(i)).OrderBy(n => n, StringComparer.Ordinal),
                list.Items.Select(i => data.NameOf(new ItemId(i.Id))).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

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
                     "/api/v1/core/persons",
                 })
        {
            var body = await app.GetRawAsync(actor, url);
            Assert.DoesNotContain(data.ForbiddenLabel(actor), body, StringComparison.Ordinal);
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
        foreach (var (name, item) in data.Items)
        foreach (var actor in new[] { app.AdultA, app.AdultB })
        {
            var request = entry.Build(data, item);
            var scoped = Assert.IsAssignableFrom<IItemScoped>(request);
            var where = $"{entry.RequestType.Name} on {name} by {data.NameOf(actor)}";

            var (_, failure) = await app.InvokeAsync(actor, request);

            if (!data.ExpectedReadable(actor).Contains(item))
            {
                Assert.True(failure is NotFoundFailure { Resource: "item" },
                    $"{where}: the rules hide this item, so it must be 'not found' — existence is never "
                    + $"disclosed — but the pipeline answered {Describe(failure)}");
                continue;
            }

            if (scoped.Access == AccessKind.Write && !data.ExpectedWritable(name, actor))
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

        foreach (var entry in RequestCatalogue.Entries)
        {
            // The catalogue is the contract a new request's author fills in: check it builds what it claims
            // before anything downstream relies on it.
            var sample = entry.Build(data, data["S-A"]);
            Assert.IsType(entry.RequestType, sample);

            if (typeof(ICommandMarker).IsAssignableFrom(entry.RequestType))
                continue;

            List<ItemId> targets = entry.IsItemScoped ? [.. data.Items.Values] : [data["S-A"]];

            foreach (var actor in new[] { app.AdultA, app.AdultB })
            foreach (var target in targets)
            {
                var (response, _) = await app.InvokeAsync(actor, entry.Build(data, target));
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

                // Text: not just the ids. A finding message or a catalogue join naming another item would
                // carry a label the identifier check cannot see. Every read gets this for free.
                var serialised = JsonSerializer.Serialize(response, ResponseJson);
                bodies[actor].Add(serialised);

                Assert.DoesNotContain(data.ForbiddenLabel(actor), serialised, StringComparison.Ordinal);
            }
        }

        // Positive control: without it, a rename or a [JsonIgnore] would empty the scan above and nothing
        // would fail. "shared-a" is a label BOTH adults are entitled to read.
        foreach (var actor in new[] { app.AdultA, app.AdultB })
            Assert.Contains(bodies[actor], body => body.Contains("shared-a", StringComparison.Ordinal));
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
            price = new { amount = 99.99m, currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        const string url = "/api/v1/subscriptions?scope=household&from=2025-01-01&to=2025-10-01";
        var forA = await app.GetAsync<ReadEndpointTests.ListResponse>(app.AdultA, url);
        var forB = await app.GetAsync<ReadEndpointTests.ListResponse>(app.AdultB, url);

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

    /// <summary>Truthful where the guard looks (Me), generous where the handler looks (Household).</summary>
    private sealed class GenerousInTheHouseholdScope(CoreDbContext db) : IVisibleItems
    {
        public async Task<IReadOnlySet<ItemId>> ForAsync(PersonId actor, VisibilityScope scope, CancellationToken ct)
        {
            if (scope != VisibilityScope.Household)
                return await new VisibleItems(db).ForAsync(actor, scope, ct);

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

    private static string Describe(Failure? failure)
        => failure is null ? "success" : $"{failure.GetType().Name}({failure.Code})";

    /// <summary>
    /// Whether a response type carries <see cref="ItemId"/> anywhere a caller can reach. Only our own types
    /// are walked: an ItemId cannot hide inside a framework type without one of ours holding it first.
    /// </summary>
    private static bool CarriesItemIds(Type type, HashSet<Type> seen)
    {
        if (type == typeof(ItemId))
            return true;
        if (!seen.Add(type) || typeof(IExposesItems).IsAssignableFrom(type))
            return false;

        IEnumerable<Type> elements = type.IsArray
            ? [type.GetElementType()!]
            : type.GetInterfaces().Concat([type])
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(i => i.GetGenericArguments()[0]);

        if (elements.Any(e => CarriesItemIds(e, seen)))
            return true;

        if (type.Assembly.GetName().Name?.StartsWith("Nid.", StringComparison.Ordinal) != true)
            return false;

        return type.GetProperties()
            .Any(p => p.GetIndexParameters().Length == 0 && CarriesItemIds(p.PropertyType, seen));
    }
}
