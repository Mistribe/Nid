using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Host.Auth;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.TestSupport;

/// <summary>
/// The real host against a freshly migrated database, with authentication replaced by a header that names
/// the acting person (<see cref="IntegrationTestGate.ActorHeader"/>). Everything else — pipeline, access
/// policy, CSRF, output guard — runs exactly as in production: unsafe requests carry a real CSRF token
/// obtained from <c>/auth/session</c>, the same way a browser would.
/// </summary>
public sealed class NidApp : IAsyncDisposable
{
    /// <summary>"Today" for every subscription this harness creates; see the comment in <see cref="StartAsync"/>.</summary>
    public static readonly LocalDate GoldenCaseToday = new(2025, 10, 15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SafeMethods = ["GET", "HEAD", "OPTIONS"];

    private readonly WebApplicationFactory<Program> factory;
    // Concurrent: a test may send its first requests in parallel (PreferencesTests).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> csrfTokens = [];

    private NidApp(
        WebApplicationFactory<Program> factory, string connectionString, PersonId a, PersonId b, PersonId d,
        FakeFxSources sources, FakePriceSources prices, FakeEnableBanking banks, FakeIrlSource irl, FakeCpiSource cpi, FakeCatalogSource catalog, FakeIconSource icons, string assetsRoot, string documentsRoot)
    {
        this.factory = factory;
        ConnectionString = connectionString;
        AdultA = a;
        AdultB = b;
        Dependent = d;
        Sources = sources;
        Prices = prices;
        Banks = banks;
        Irl = irl;
        Cpi = cpi;
        Catalog = catalog;
        Icons = icons;
        AssetsRoot = assetsRoot;
        DocumentsRoot = documentsRoot;
    }

    /// <summary>The database this harness runs against, for tests that read rows back directly.</summary>
    public string ConnectionString { get; }

    /// <summary>The host's root provider; create a scope before resolving scoped services.</summary>
    public IServiceProvider Services => factory.Services;

    /// <summary>The in-memory server, for a request that needs a chosen remote address (<c>SendAsync</c>).</summary>
    public Microsoft.AspNetCore.TestHost.TestServer Server => factory.Server;

    /// <summary>The household's admin (the first adult, administration-and-audit.md §2.1).</summary>
    public PersonId AdultA { get; }

    /// <summary>An adult who is not admin.</summary>
    public PersonId AdultB { get; }
    public PersonId Dependent { get; }

    /// <summary>The offline <see cref="IFxSourceClient"/> this host runs against; every request fails unless a test configures it.</summary>
    public FakeFxSources Sources { get; }

    /// <summary>The offline <see cref="IPriceSourceClient"/> this host runs against; every request fails unless a test serves pages.</summary>
    public FakePriceSources Prices { get; }

    /// <summary>The offline <see cref="Nid.Banking.Contracts.IEnableBankingClient"/> this host runs against; every call fails unless a test scripts it.</summary>
    public FakeEnableBanking Banks { get; }

    /// <summary>The offline <see cref="IIrlSourceClient"/> this host runs against; every request fails unless a test serves the series.</summary>
    public FakeIrlSource Irl { get; }

    /// <summary>The offline <see cref="ICpiSourceClient"/> this host runs against; every request fails unless a test serves the series.</summary>
    public FakeCpiSource Cpi { get; }

    /// <summary>The offline <see cref="ICatalogSourceClient"/> this host runs against; every fetch fails unless a test serves files.</summary>
    public FakeCatalogSource Catalog { get; }

    /// <summary>The offline <see cref="ICatalogIconSource"/> this host runs against; every icon is unreachable unless a test serves it.</summary>
    public FakeIconSource Icons { get; }

    /// <summary>This host's asset store (1C2c): a temporary folder of its own, deleted when the host is disposed.</summary>
    public string AssetsRoot { get; }

    /// <summary>This host's document storage folder (<c>Documents:Storage:Path</c>), deleted when the host is disposed.</summary>
    public string DocumentsRoot { get; }

    /// <param name="overrides">
    /// Applied last, so a test can replace a registered service. The leak bench uses it to install a
    /// deliberately broken <c>IVisibleItems</c> and watch the output guard catch it (access-tests.md §5).
    /// </param>
    /// <param name="settings">Extra host configuration, applied after the fixed settings below (e.g. <c>Jobs:Enabled</c> for Task 7).</param>
    public static async Task<NidApp> StartAsync(
        PostgresFixture fixture, Action<IServiceCollection>? overrides = null, IReadOnlyDictionary<string, string>? settings = null)
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        var sources = FakeFxSources.Offline();
        var prices = FakePriceSources.Offline();
        var banks = FakeEnableBanking.Offline();
        var irl = FakeIrlSource.Offline();
        var cpi = FakeCpiSource.Offline();
        var catalog = FakeCatalogSource.Offline();
        var icons = FakeIconSource.Offline();
        var assetsRoot = Path.Combine(Path.GetTempPath(), "nid-assets-" + Guid.NewGuid().ToString("N"));
        var documentsRoot = Path.Combine(Path.GetTempPath(), "nid-documents-" + Guid.NewGuid().ToString("N"));

        await using (var db = PostgresFixture.NewCoreContext(connectionString))
        {
            db.Persons.AddRange(
                NewPerson(out var a, "adult", "sub-a", isAdmin: true),
                NewPerson(out var b, "adult", "sub-b"),
                NewPerson(out var d, "dependent", null));
            await db.SaveChangesAsync();

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(IntegrationTestGate.Environment);
                builder.UseSetting("ConnectionStrings:nid", connectionString);
                builder.UseSetting("Auth:PublicUrl", "https://test.localhost");
                builder.UseSetting("Auth:Authority", "https://auth.test.localhost");
                builder.UseSetting("Auth:ClientSecret", "test-secret");
                builder.UseSetting("Auth:CsrfKey", "test-csrf-key-0123456789abcdef0123456789");
                // No test host runs Quartz unless a test asks for it (Task 7), and none reaches the network.
                builder.UseSetting("Jobs:Enabled", "false");
                builder.UseSetting("Assets:Root", assetsRoot);
                builder.UseSetting("Documents:Storage:Path", documentsRoot);
                // The database is a copy of the template PostgresFixture migrated once: checking eight migration
                // histories again cost every test about 0.2 s.
                builder.UseSetting("Health:SkipMigrations", "true");
                foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                    builder.UseSetting(key, value);
                // The golden-case fixtures (NewSpotifyGoldenCaseAsync) are dated around October 2025, matching
                // docs/subscription-data-model.md and SubscriptionStoreTests. "today" must stay pinned there —
                // wiring the real SystemClock would make NextCharge()'s IsProjected flip false once the wall
                // clock passes those fixed dates, turning a correct assertion into a flaky one.
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IClock>(new FixedClock(GoldenCaseToday.AtMidnight().InUtc().ToInstant()));
                    services.AddSingleton<IFxSourceClient>(sources);
                    services.AddSingleton<IPriceSourceClient>(prices);
                    services.AddSingleton<Nid.Banking.Contracts.IEnableBankingClient>(banks);
                    services.AddSingleton<IIrlSourceClient>(irl);
                    services.AddSingleton<ICpiSourceClient>(cpi);
                    services.AddSingleton<ICatalogSourceClient>(catalog);
                    services.AddSingleton<ICatalogIconSource>(icons);
                    // No test reaches the identity provider (https://auth.test.localhost does not exist).
                    services.AddSingleton<Nid.Host.Admin.IIdentityProviderProbe>(new ReachableIdentityProvider());
                    // Production keeps reflection behind the source-generated contexts (Api/Json), so a type missing from
                    // them still answers there; here it fails the test that reaches it, as it would in a NativeAOT build.
                    services.PostConfigure<JsonOptions>(o => RemoveReflection(o.SerializerOptions.TypeInfoResolverChain));
                    overrides?.Invoke(services);
                });
            });

            return new NidApp(factory, connectionString, a, b, d, sources, prices, banks, irl, cpi, catalog, icons, assetsRoot, documentsRoot);
        }
    }

    private static void RemoveReflection(IList<IJsonTypeInfoResolver> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
            if (chain[i] is DefaultJsonTypeInfoResolver)
                chain.RemoveAt(i);
    }

    private static PersonRow NewPerson(out PersonId id, string kind, string? subject, bool isAdmin = false)
    {
        var guid = Guid.CreateVersion7();
        id = new PersonId(guid);
        return new PersonRow
        {
            Id = guid, Kind = kind, OidcSubject = subject, IsAdmin = isAdmin,
            DisplayName = kind + "-" + guid.ToString("N")[..4],
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
    }

    private HttpClient ClientFor(PersonId actor)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(IntegrationTestGate.ActorHeader, actor.Value.ToString());
        return client;
    }

    public async Task<T> GetAsync<T>(PersonId actor, string url)
    {
        var response = await RawAsync(actor, HttpMethod.Get, url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task<string> GetRawAsync(PersonId actor, string url)
        => await (await RawAsync(actor, HttpMethod.Get, url)).Content.ReadAsStringAsync();

    /// <param name="headers">Extra request headers, as a browser would send them (<c>User-Agent</c>, a proxy's <c>X-Forwarded-For</c>).</param>
    public async Task<HttpResponseMessage> RawAsync(
        PersonId actor, HttpMethod method, string url, object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var client = ClientFor(actor);
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        if (!SafeMethods.Contains(method.Method))
            request.Headers.Add(CsrfMiddleware.HeaderName, await CsrfTokenForAsync(actor));
        return await client.SendAsync(request);
    }

    /// <summary>Like <see cref="RawAsync"/>, with <paramref name="content"/> sent as it is (raw bytes, a body with no length).</summary>
    public async Task<HttpResponseMessage> RawContentAsync(PersonId actor, HttpMethod method, string url, HttpContent content)
    {
        using var client = ClientFor(actor);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (!SafeMethods.Contains(method.Method))
            request.Headers.Add(CsrfMiddleware.HeaderName, await CsrfTokenForAsync(actor));
        return await client.SendAsync(request);
    }

    /// <summary>A GET as <paramref name="actor"/> with extra request headers (<c>If-None-Match</c>, for instance).</summary>
    public async Task<HttpResponseMessage> GetWithHeadersAsync(PersonId actor, string url, IReadOnlyDictionary<string, string> headers)
    {
        using var client = ClientFor(actor);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await client.SendAsync(request);
    }

    /// <summary>A GET as <paramref name="actor"/> whose redirect is returned rather than followed (the bank callback's 302).</summary>
    public async Task<HttpResponseMessage> GetUnfollowedAsync(PersonId actor, string url)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(IntegrationTestGate.ActorHeader, actor.Value.ToString());
        return await client.GetAsync(url);
    }

    /// <summary>A request with no actor header: what an unauthenticated caller gets.</summary>
    public async Task<HttpResponseMessage> RawAnonymousAsync(HttpMethod method, string url)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        return await client.SendAsync(request);
    }

    /// <summary>A client of the test host with no actor header and no cookie (4B: the MCP client brings its own token).</summary>
    public HttpClient CreateHttpClient() => factory.CreateClient();

    /// <summary>
    /// A request authenticated by a personal access token (4A), with no CSRF header. <paramref name="withSessionOf"/>
    /// also sends that person's test session, to check that the token alone decides (Review Focus 1).
    /// </summary>
    public async Task<HttpResponseMessage> BearerAsync(string token, HttpMethod method, string url, object? body = null, PersonId? withSessionOf = null)
    {
        using var client = withSessionOf is { } person ? ClientFor(person) : factory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(request);
    }

    /// <summary>Like <see cref="BearerAsync"/>, with any content (a multipart form).</summary>
    public async Task<HttpResponseMessage> BearerContentAsync(string token, HttpMethod method, string url, HttpContent content)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await client.SendAsync(request);
    }

    /// <summary>Like <see cref="RawAsync"/>, with a body sent exactly as given.</summary>
    public async Task<HttpResponseMessage> RawTextAsync(PersonId actor, HttpMethod method, string url, string json)
    {
        using var client = ClientFor(actor);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (!SafeMethods.Contains(method.Method))
            request.Headers.Add(CsrfMiddleware.HeaderName, await CsrfTokenForAsync(actor));
        return await client.SendAsync(request);
    }

    /// <summary>
    /// A real CSRF token, bound to the actor and their session epoch, obtained from <c>/auth/session</c>
    /// exactly as a browser would (see <c>deploy/dev/smoke.sh</c>) — never a bypass of
    /// <c>CsrfMiddleware</c>, which runs unmodified here. Cached per actor for the life of this harness: the
    /// token depends only on the person and their (unchanging, within a test) session epoch.
    /// </summary>
    private async Task<string> CsrfTokenForAsync(PersonId actor)
    {
        if (csrfTokens.TryGetValue(actor.Value, out var cached))
            return cached;

        var response = await RawAsync(actor, HttpMethod.Get, "/auth/session");
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = session.GetProperty("csrf").GetString()!;
        csrfTokens[actor.Value] = token;
        return token;
    }

    /// <summary>Creates the Core item and the subscription through the real API.</summary>
    public async Task<ItemId> NewSubscriptionAsync(string name, PersonId owner, Visibility visibility)
    {
        var response = await RawAsync(owner, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = name,
            visibility = visibility.ToString().ToLowerInvariant(),
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
            autoRenew = true,
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Creates a subscription whose owners — and their shares — are named explicitly, through the real API.
    /// <see cref="NewSubscriptionAsync"/> covers the single-owner case; the leak bench needs the joint and
    /// dependent-owned shapes of <c>access-tests.md</c> §2 too.
    /// </summary>
    public async Task<ItemId> NewOwnedSubscriptionAsync(
        string name, Visibility visibility, params (PersonId Person, int ShareBp)[] owners)
    {
        // Created by an adult who owns it, or by A when the owner is the dependent (a dependent has no login).
        var creator = owners.Select(o => o.Person).FirstOrDefault(p => p == AdultA || p == AdultB, AdultA);

        var response = await RawAsync(creator, HttpMethod.Post, "/api/v1/subscriptions", new
        {
            displayName = name,
            visibility = visibility.ToString().ToLowerInvariant(),
            owners = owners.Select(o => new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray(),
            autoRenew = true,
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>Creates the Core item and the envelope through the real API (2B1a).</summary>
    public async Task<ItemId> NewEnvelopeAsync(string name, PersonId owner, Visibility visibility, string currency = "EUR")
    {
        var response = await RawAsync(owner, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind = "savings", currency,
            visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        });
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ItemId(json.RootElement.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// An envelope owned by <paramref name="owners"/>, through the real API — the envelope counterpart of
    /// <see cref="NewOwnedSubscriptionAsync"/>, created by an adult owner, or by A when only the dependent owns it.
    /// </summary>
    public async Task<ItemId> NewEnvelopeAsync(
        string name, Visibility visibility, params (PersonId Person, int ShareBp)[] owners)
    {
        var creator = owners.Select(o => o.Person).FirstOrDefault(p => p == AdultA || p == AdultB, AdultA);

        var response = await RawAsync(creator, HttpMethod.Post, "/api/v1/investments/envelopes", new
        {
            displayName = name, kind = "savings", currency = "EUR",
            visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = owners.Select(o => new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray(),
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>Creates the Core item and the income source through the real API (2C1), with no phase.</summary>
    public async Task<ItemId> NewIncomeSourceAsync(
        string name, PersonId owner, string visibility = "shared", string currency = "EUR", string kind = "salary")
    {
        var response = await RawAsync(owner, HttpMethod.Post, "/api/v1/income/sources", new
        {
            displayName = name, kind, currency, visibility,
            owners = new[] { new { personId = owner.Value, shareBp = 10_000 } },
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// An income source owned by <paramref name="owners"/>, through the real API — the income counterpart of
    /// <see cref="NewOwnedSubscriptionAsync"/>, created by an adult owner, or by A when only the dependent owns it.
    /// It has one monthly phase whose note is <paramref name="name"/>, so a textual scan finds a phase read by the wrong adult.
    /// </summary>
    public async Task<ItemId> NewIncomeSourceAsync(
        string name, Visibility visibility, params (PersonId Person, int ShareBp)[] owners)
        => await NewIncomeSourceAsync(name, "salary", visibility, owners);

    /// <summary>The same, of the given <paramref name="kind"/> (<c>pension</c> for a retirement read).</summary>
    public async Task<ItemId> NewIncomeSourceAsync(
        string name, string kind, Visibility visibility, params (PersonId Person, int ShareBp)[] owners)
    {
        var creator = owners.Select(o => o.Person).FirstOrDefault(p => p == AdultA || p == AdultB, AdultA);

        var response = await RawAsync(creator, HttpMethod.Post, "/api/v1/income/sources", new
        {
            displayName = name, kind, currency = "EUR",
            visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = owners.Select(o => new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray(),
            phases = new[]
            {
                new
                {
                    label = "Salaire", startsOn = "2025-01-01", netAmount = new { amount = "1000", currency = "EUR" },
                    every = new { count = 1, unit = "month" }, note = name,
                },
            },
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A loan owned by <paramref name="owners"/>, through the real API (3A2), created by an adult owner, or by A when only the
    /// dependent owns it: 10 000 € at 1 % over 24 payments from 2025-01-15, named <paramref name="name"/> so a textual scan
    /// finds a loan read by the wrong adult.
    /// </summary>
    public async Task<ItemId> NewLoanAsync(string name, Visibility visibility, params (PersonId Person, int ShareBp)[] owners)
    {
        var creator = owners.Select(o => o.Person).FirstOrDefault(p => p == AdultA || p == AdultB, AdultA);

        var response = await RawAsync(creator, HttpMethod.Post, "/api/v1/realestate/loans", new
        {
            name, currency = "EUR", visibility = visibility == Visibility.Private ? "private" : "shared",
            borrowers = owners.Select(o => new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray(),
            principal = "10000", disbursedOn = "2025-01-01", firstPaymentDate = "2025-01-15", totalPayments = 24,
            rounding = "half_up", firstPeriod = "full",
            phases = new[] { new { seq = 1, mode = "annuity", annualRate = "0.01", rateType = "fixed" } },
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A property owned by <paramref name="owners"/>, through the real API (3C1), created by an adult owner, or by A when only the
    /// dependent owns it: a rental bought 100 000 € on 2020-01-15, named <paramref name="name"/> so a textual scan finds a property
    /// read by the wrong adult.
    /// </summary>
    public async Task<ItemId> NewPropertyAsync(string name, Visibility visibility, params (PersonId Person, int ShareBp)[] owners)
    {
        var creator = owners.Select(o => o.Person).FirstOrDefault(p => p == AdultA || p == AdultB, AdultA);

        var response = await RawAsync(creator, HttpMethod.Post, "/api/v1/realestate/properties", new
        {
            name, currency = "EUR", visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = owners.Select(o => new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray(),
            type = "rental", purchaseDate = "2020-01-15", purchasePrice = "100000",
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A savings goal of 10 000 through the real API, created by <paramref name="actor"/>, owned by <paramref name="owners"/> (the
    /// actor alone by default) and funded by <paramref name="funding"/> (a free goal without).
    /// </summary>
    public async Task<ItemId> NewGoalAsync(
        string name, PersonId actor, Visibility visibility = Visibility.Shared, IReadOnlyList<(ItemId Envelope, int ShareBp)>? funding = null,
        IReadOnlyList<(PersonId Person, int ShareBp)>? owners = null)
    {
        var response = await RawAsync(actor, HttpMethod.Post, "/api/v1/goals", new
        {
            name, kind = "savings", targetAmount = "10000", visibility = visibility == Visibility.Private ? "private" : "shared",
            owners = (owners ?? [(actor, 10_000)]).Select(o => new { personId = o.Person.Value, shareBp = o.ShareBp }).ToArray(),
            funding = (funding ?? []).Select(f => new { envelopeId = f.Envelope.Value, shareBp = f.ShareBp }).ToArray(),
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return new ItemId(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Runs a request through the real Synapse pipeline as <paramref name="actor"/> — the same invoker, the
    /// same behaviors and the same access policy the HTTP entry point uses, with the actor established the
    /// way <c>ActorFilter</c> establishes it. Returns the failure the pipeline produced, or <c>null</c> on
    /// success. This is how the leak bench reaches requests that have no endpoint of their own yet.
    /// </summary>
    public async Task<(object? Response, Failure? Failure)> InvokeAsync(
        PersonId actor, object request, CancellationToken ct = default)
    {
        var contract = request.GetType().GetInterfaces().Single(
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        await using var scope = factory.Services.CreateAsyncScope();
        var person = await scope.ServiceProvider.GetRequiredService<IPersonRegistry>().FindAsync(actor, ct);
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.Browser(actor, person?.IsAdmin == true);

        var invoke = typeof(NidApp)
            .GetMethod(nameof(InvokeCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(contract.GetGenericArguments()[0]);

        return await (Task<(object?, Failure?)>)invoke.Invoke(
            null, [scope.ServiceProvider.GetRequiredService<IInvoker>(), request, ct])!;
    }

    private static async Task<(object?, Failure?)> InvokeCoreAsync<TResponse>(
        IInvoker invoker, IRequest<TResponse> request, CancellationToken ct)
        where TResponse : notnull
    {
        var result = await invoker.InvokeAsync<TResponse>(request, ct);
        if (result.TryGetFailure(out var failure))
            return (null, failure!);

        result.TryGetValue(out var value);
        return (value, null);
    }

    /// <summary>
    /// Writes one purchase granting a month to each of <paramref name="grantTo"/> straight through the store,
    /// bypassing the pipeline on purpose: the leak bench needs a purchase spanning ANY pair of the dataset,
    /// including pairs no single adult may write — a state the database can hold (the two items were writable
    /// together when it was recorded, and one turned private since) and which the delete path must survive.
    /// </summary>
    public async Task<PurchaseId> SeedPurchaseGrantingToAsync(params ItemId[] grantTo)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System; // audited rows need an actor (1F3a U4)
        var store = scope.ServiceProvider.GetRequiredService<ISubscriptionStore>();

        var purchase = new Purchase(
            PurchaseId.New(), new LocalDate(2025, 1, 1), "bench-bundle",
            Money.Create(1m, Currency.Eur).Ok(), new Payer(PayerType.Household, null), PurchaseKind.PrepaidTerm,
            grantTo.Select(item => new Grant(
                item, Duration.Create(1, DurationUnit.Month).Ok(), CoverageSource.Paid, null)).ToList(),
            null);

        return (await store.AddPurchaseAsync(purchase, RowOrigin.None, CancellationToken.None)).Ok();
    }

    /// <summary>
    /// Removes a subscription's insurance straight through the store, bypassing the pipeline: the leak bench sets a link per request
    /// and a link kept from the request before would turn a new link into « the same one », which needs no read of the property.
    /// </summary>
    public async Task ClearInsuranceAsync(ItemId subscription)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<ISubscriptionStore>().DeleteInsuranceAsync(subscription, CancellationToken.None); // not found: none to clear
    }

    public async Task<ItemId> NewMonthlyAsync(PersonId owner, decimal price)
    {
        var id = await NewSubscriptionAsync($"monthly-{price}", owner, Visibility.Shared);
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = price.ToString(CultureInfo.InvariantCulture), currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    public async Task<ItemId> NewSpotifyGoldenCaseAsync(PersonId owner)
    {
        var id = await NewSubscriptionAsync("Spotify Premium Duo", owner, Visibility.Shared);
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2024-06-28", endsOn = "2025-06-28",
            price = new { amount = "15.17", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-06-28",
            price = new { amount = "17.20", currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2025-10-10", label = "Spotify gift card 3 months", kind = "gift",
            price = new { amount = "0", currency = "EUR" },
            payer = new { type = "external" },
            grants = new[] { new { subscriptionId = id.Value, duration = new { count = 3, unit = "month" }, source = "gift" } },
        })).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>One purchase granting a month to two different subscriptions.</summary>
    public async Task<(ItemId first, ItemId second)> NewSharedBundleAsync(PersonId owner, decimal price)
    {
        var first = await NewSubscriptionAsync("shared-first", owner, Visibility.Shared);
        var second = await NewSubscriptionAsync("shared-second", owner, Visibility.Shared);
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{first.Value}/purchases", new
        {
            date = "2025-01-01", label = "two services", kind = "prepaid_term",
            price = new { amount = price.ToString(CultureInfo.InvariantCulture), currency = "EUR" },
            payer = new { type = "household" },
            grants = new[]
            {
                new { subscriptionId = first.Value, duration = new { count = 1, unit = "month" }, source = "paid" },
                new { subscriptionId = second.Value, duration = new { count = 1, unit = "month" }, source = "paid" },
            },
        })).EnsureSuccessStatusCode();
        return (first, second);
    }

    /// <summary>Writes a provider row directly, as a sync or the adoption would have: a test's way to a known catalog.</summary>
    public async Task SeedProviderAsync(string key, string name, ProviderOrigin origin, PersonId? createdBy = null, bool archived = false)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(ConnectionString);
        subs.Providers.Add(new ProviderRow
        {
            Key = key,
            Origin = origin.ToWire(),
            Name = name,
            LabelKeys = [CatalogLimits.OtherLabel],
            CatalogCommit = origin == ProviderOrigin.Catalog ? CatalogFixtures.PinnedCommit : null,
            CreatedBy = origin == ProviderOrigin.Custom ? (createdBy ?? AdultA).Value : null,
            ArchivedAt = archived ? SystemClock.Instance.GetCurrentInstant() : null,
        });
        await subs.SaveChangesAsync();
    }

    /// <summary>Sets a subscription's provider key in the database, bypassing every check: a state an earlier write left.</summary>
    public async Task LinkProviderAsync(ItemId item, string? key)
    {
        await using var subs = PostgresFixture.NewSubscriptionsContext(ConnectionString);
        await subs.Subscriptions.Where(s => s.ItemId == item.Value).ExecuteUpdateAsync(s => s.SetProperty(r => r.ProviderKey, key));
    }

    /// <summary>
    /// Stores <paramref name="webp"/> in this host's asset store and links it to <paramref name="key"/> as the icon
    /// pass would (1C2c): what a test of the route or the views starts from. Answers the hash.
    /// </summary>
    public async Task<string> SeedProviderIconAsync(string key, byte[] webp)
    {
        var hash = await factory.Services.GetRequiredService<IAssetStore>().PutAsync(webp, "image/webp", CancellationToken.None);
        var url = $"https://1000logos.net/{key}.png";
        await using var subs = PostgresFixture.NewSubscriptionsContext(ConnectionString);
        await subs.Providers.Where(p => p.Key == key).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.IconUrl, url)
            .SetProperty(p => p.IconSource, url)
            .SetProperty(p => p.IconAsset, hash));
        return hash;
    }

    /// <summary>
    /// A fresh custom provider that <paramref name="item"/>, and nothing else, uses: only that item makes it visible. Its
    /// creator is nobody of the dataset — a creator always sees their provider (§3.5 as amended), which would hide the
    /// very refusal the provider axis checks.
    /// </summary>
    public async Task<string> SeedCustomProviderUsedByAsync(ItemId item)
    {
        var key = "bench-" + Guid.NewGuid().ToString("N")[..12];
        await SeedProviderAsync(key, key, ProviderOrigin.Custom, createdBy: PersonId.New());
        await LinkProviderAsync(item, key);
        return key;
    }

    /// <summary>The adult's current session epoch, read from the database.</summary>
    public async Task<int> EpochOfAsync(PersonId person)
    {
        await using var core = PostgresFixture.NewCoreContext(ConnectionString);
        return (await core.Persons.SingleAsync(p => p.Id == person.Value)).SessionEpoch;
    }

    public ValueTask DisposeAsync()
    {
        factory.Dispose();
        try
        {
            foreach (var root in new[] { AssetsRoot, DocumentsRoot })
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open elsewhere: the operating system cleans its temporary folder.
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FixedClock(Instant instant) : IClock
    {
        public Instant GetCurrentInstant() => instant;
    }

    private sealed class ReachableIdentityProvider : Nid.Host.Admin.IIdentityProviderProbe
    {
        public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
