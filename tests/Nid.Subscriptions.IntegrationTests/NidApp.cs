using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>
/// The real host against a freshly migrated database, with authentication replaced by a header that names
/// the acting person. Everything else — pipeline, access policy, output guard — runs exactly as in production.
/// </summary>
public sealed class NidApp : IAsyncDisposable
{
    public const string ActorHeader = "X-Nid-Test-Actor";

    /// <summary>"Today" for every subscription this harness creates; see the comment in <see cref="StartAsync"/>.</summary>
    public static readonly LocalDate GoldenCaseToday = new(2025, 10, 15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> factory;

    private NidApp(WebApplicationFactory<Program> factory, PersonId a, PersonId b, PersonId d)
        => (this.factory, AdultA, AdultB, Dependent) = (factory, a, b, d);

    public PersonId AdultA { get; }
    public PersonId AdultB { get; }
    public PersonId Dependent { get; }

    public static async Task<NidApp> StartAsync(PostgresFixture fixture)
    {
        var connectionString = await fixture.NewConnectionStringAsync();

        await using (var db = PostgresFixture.NewCoreContext(connectionString))
        {
            db.Persons.AddRange(
                NewPerson(out var a, "adult", "sub-a"),
                NewPerson(out var b, "adult", "sub-b"),
                NewPerson(out var d, "dependent", null));
            await db.SaveChangesAsync();

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("IntegrationTest");
                builder.UseSetting("ConnectionStrings:nid", connectionString);
                builder.UseSetting("Auth:PublicUrl", "https://test.localhost");
                builder.UseSetting("Auth:Authority", "https://auth.test.localhost");
                builder.UseSetting("Auth:ClientSecret", "test-secret");
                builder.UseSetting("Auth:CsrfKey", "test-csrf-key-0123456789abcdef0123456789");
                // The golden-case fixtures (NewSpotifyGoldenCaseAsync) are dated around October 2025, matching
                // docs/subscription-data-model.md and SubscriptionStoreTests. "today" must stay pinned there —
                // wiring the real SystemClock would make NextCharge()'s IsProjected flip false once the wall
                // clock passes those fixed dates, turning a correct assertion into a flaky one.
                builder.ConfigureServices(services =>
                    services.AddSingleton<IClock>(new FixedClock(GoldenCaseToday.AtMidnight().InUtc().ToInstant())));
            });

            return new NidApp(factory, a, b, d);
        }
    }

    private static PersonRow NewPerson(out PersonId id, string kind, string? subject)
    {
        var guid = Guid.CreateVersion7();
        id = new PersonId(guid);
        return new PersonRow
        {
            Id = guid, Kind = kind, OidcSubject = subject,
            DisplayName = kind + "-" + guid.ToString("N")[..4],
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
    }

    private HttpClient ClientFor(PersonId actor)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ActorHeader, actor.Value.ToString());
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

    public async Task<HttpResponseMessage> RawAsync(PersonId actor, HttpMethod method, string url, object? body = null)
    {
        using var client = ClientFor(actor);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await client.SendAsync(request);
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

    public async Task<ItemId> NewMonthlyAsync(PersonId owner, decimal price)
    {
        var id = await NewSubscriptionAsync($"monthly-{price}", owner, Visibility.Shared);
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount = price, currency = "EUR" },
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
            price = new { amount = 15.17m, currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-06-28",
            price = new { amount = 17.20m, currency = "EUR" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        (await RawAsync(owner, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/purchases", new
        {
            date = "2025-10-10", label = "Spotify gift card 3 months", kind = "gift",
            price = new { amount = 0m, currency = "EUR" },
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
            price = new { amount = price, currency = "EUR" },
            payer = new { type = "household" },
            grants = new[]
            {
                new { subscriptionId = first.Value, duration = new { count = 1, unit = "month" }, source = "paid" },
                new { subscriptionId = second.Value, duration = new { count = 1, unit = "month" }, source = "paid" },
            },
        })).EnsureSuccessStatusCode();
        return (first, second);
    }

    public ValueTask DisposeAsync()
    {
        factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class FixedClock(Instant instant) : IClock
    {
        public Instant GetCurrentInstant() => instant;
    }
}
