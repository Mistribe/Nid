using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.EnableBanking;
using Nid.Platform.Outbound;
using NodaTime;

namespace Nid.Core.IntegrationTests;

/// <summary>The client behind a stub handler and synthetic vectors shaped like the API reference: no network.</summary>
public class EnableBankingClientTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 29, 9, 0);

    private sealed record Seen(HttpMethod Method, Uri Url, HttpRequestHeaders Headers, string Body);

    /// <summary>Answers with each body in turn, the last one again after that.</summary>
    private sealed class StubHandler(HttpStatusCode status, params string[] bodies) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.Method, request.RequestUri!, request.Headers, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(bodies[Math.Min(Requests.Count, bodies.Length) - 1]) };
        }
    }

    private static string Vector(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "enablebanking", name));

    private static (EnableBankingClient Client, StubHandler Handler) Client(HttpStatusCode status, string body, string? key = null, string? second = null)
    {
        var handler = new StubHandler(status, second is null ? [body] : [body, second]);
        using var rsa = RSA.Create(2048);
        var http = new SafeHttpClient(new HttpMessageInvoker(handler), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance);
        return (new EnableBankingClient(http, new BankingOptions { PrivateKey = key ?? rsa.ExportPkcs8PrivateKeyPem() }, new FixedClock(Now)), handler);
    }

    private static T Ok<T>(UnambitiousFx.Functional.Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetValue(out var value));
        return value!;
    }

    [Fact]
    public async Task Aspsps_are_parsed_with_their_maximum_consent_validity()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("aspsps-fr.json"));

        var list = Ok(await client.AspspsAsync("app", "FR", default));

        Assert.Equal(new Aspsp("Banque Exemple", "FR", "https://enablebanking.com/brands/FR/BanqueExemple/", 15552000), list[0]);
        Assert.Null(list[1].MaximumConsentSeconds);
        Assert.Equal("https://api.enablebanking.com/aspsps?country=FR", handler.Requests.Single().Url.AbsoluteUri);
    }

    [Fact]
    public async Task StartAuth_posts_the_access_the_bank_the_state_and_the_redirect_and_returns_the_url()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("auth.json"));

        var start = Ok(await client.StartAuthAsync("app", new Aspsp("Banque Exemple", "FR", null, null), Instant.FromUtc(2027, 4, 27, 9, 30), "st4te", "https://nid.example/api/v1/banking/callback", default));

        Assert.Equal("https://bank.example/authorize?client=synthetic&state=abc", start.Url);
        var request = handler.Requests.Single();
        Assert.Equal((HttpMethod.Post, "https://api.enablebanking.com/auth"), (request.Method, request.Url.AbsoluteUri));
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("2027-04-27T09:30:00Z", body.GetProperty("access").GetProperty("valid_until").GetString());
        Assert.False(body.GetProperty("access").TryGetProperty("balances", out _));
        Assert.Equal("Banque Exemple", body.GetProperty("aspsp").GetProperty("name").GetString());
        Assert.Equal("FR", body.GetProperty("aspsp").GetProperty("country").GetString());
        Assert.Equal(("st4te", "https://nid.example/api/v1/banking/callback", "personal"),
            (body.GetProperty("state").GetString(), body.GetProperty("redirect_url").GetString(), body.GetProperty("psu_type").GetString()));
    }

    [Fact]
    public async Task A_session_is_parsed_and_an_account_without_uid_is_left_out()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("session.json"));

        var session = Ok(await client.CreateSessionAsync("app", "the-code", default));

        Assert.Equal("0b9f6c2e-3f64-4d3a-8a51-6d1a5c7b9e20", session.SessionId);
        Assert.Equal(Instant.FromUtc(2027, 4, 27, 9, 30), session.ValidUntil);
        Assert.Equal(
            [
                new SessionAccount("3e0b5f58-52f1-4a1c-9b0e-0a8c8d9d1f01", "aGFzaC1jb21wdGUtY291cmFudA", "FR7630006000011234567890189", "Compte courant", "CACC", "EUR"),
                new SessionAccount("5a9d6d0e-71c4-4bb5-8d49-9c1b3b6f2a02", "aGFzaC1saXZyZXQ", "FR1420041010050500013M02606", "M DUPONT JEAN", "SVGS", "EUR"),
            ],
            session.Accounts);
        Assert.Equal("the-code", JsonDocument.Parse(handler.Requests.Single().Body).RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"session_id":"s","access":{"valid_until":"2027-04-27T09:30:00Z"},"accounts":[{"uid":"u","identification_hash":null,"cash_account_type":"CACC","currency":"EUR"}]}""")]
    [InlineData("""{"session_id":"s","access":{"valid_until":"2027-04-27T09:30:00Z"},"accounts":[{"uid":"u","cash_account_type":"CACC","currency":"EUR"}]}""")]
    [InlineData("""{"session_id":"s","accounts":[]}""")]
    [InlineData("""{"session_id":null,"access":{"valid_until":"2027-04-27T09:30:00Z"},"accounts":[]}""")]
    public async Task A_session_missing_a_required_field_is_a_provider_error(string body)
    {
        var (client, _) = Client(HttpStatusCode.OK, body);

        Assert.Equal(BankingCodes.ProviderError, FailureOf(await client.CreateSessionAsync("app", "code", default)));
    }

    [Fact]
    public async Task Balances_are_parsed_with_an_invariant_decimal_and_the_reference_date()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("balances.json"));
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            var balances = Ok(await client.BalancesAsync("app", "sess", "3e0b5f58-52f1-4a1c-9b0e-0a8c8d9d1f01", null, default));

            Assert.Equal([new ProviderBalance("ITAV", 1234.56m, "EUR", new LocalDate(2026, 10, 28)), new ProviderBalance("CLBD", -42.10m, "EUR", null)], balances);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("https://api.enablebanking.com/accounts/3e0b5f58-52f1-4a1c-9b0e-0a8c8d9d1f01/balances", handler.Requests.Single().Url.AbsoluteUri);
    }

    [Fact]
    public async Task Transactions_read_every_page_keep_booked_lines_and_sign_the_amount()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("transactions-page1.json"), second: Vector("transactions-page2.json"));

        var lines = Ok(await client.TransactionsAsync("app", "sess", "uid-1", new LocalDate(2026, 10, 1), null, default)).Lines;

        Assert.Equal(
        [
            new ProviderTransaction("ref-1", new LocalDate(2026, 10, 27), new LocalDate(2026, 10, 28), -13.49m, "EUR", "PRLV SEPA NETFLIX OCT 2026", "NETFLIX INTERNATIONAL", "PRLV SEPA NETFLIX OCT 2026"),
            new ProviderTransaction("ref-2", new LocalDate(2026, 10, 26), null, 1500.00m, "EUR", "ACME SAS", "ACME SAS", null),
            // No booking date: the value date stands in; no remittance nor party: the code's description.
            new ProviderTransaction("ref-3", new LocalDate(2026, 10, 20), new LocalDate(2026, 10, 20), -2.50m, "EUR", "Frais", null, null),
        ], lines);
        Assert.Equal(["https://api.enablebanking.com/accounts/uid-1/transactions?date_from=2026-10-01",
            "https://api.enablebanking.com/accounts/uid-1/transactions?date_from=2026-10-01&continuation_key=page-2-key"], handler.Requests.Select(r => r.Url.AbsoluteUri));
    }

    [Fact]
    public async Task Transactions_without_a_start_date_send_none_and_a_line_without_any_label_gets_the_placeholder()
    {
        var (client, handler) = Client(HttpStatusCode.OK,
            """{"transactions":[{"transaction_amount":{"currency":"EUR","amount":"1"},"credit_debit_indicator":"CRDT","status":"BOOK","booking_date":"2026-10-01"}]}""");

        var lines = Ok(await client.TransactionsAsync("app", "s", "u", null, null, default)).Lines;

        Assert.Equal("(sans libellé)", Assert.Single(lines).Label);
        Assert.Equal("https://api.enablebanking.com/accounts/u/transactions", handler.Requests.Single().Url.AbsoluteUri);
    }

    [Fact]
    public async Task A_debit_is_negative_and_a_credit_positive_whatever_sign_the_bank_sent()
    {
        var (client, _) = Client(HttpStatusCode.OK, """
            {"transactions":[
              {"transaction_amount":{"currency":"EUR","amount":"-5.00"},"credit_debit_indicator":"DBIT","status":"BOOK","booking_date":"2026-10-01"},
              {"transaction_amount":{"currency":"EUR","amount":"-7.00"},"credit_debit_indicator":"CRDT","status":"BOOK","booking_date":"2026-10-01"}]}
            """);

        Assert.Equal([-5.00m, 7.00m], Ok(await client.TransactionsAsync("app", "s", "u", null, null, default)).Lines.Select(t => t.Amount));
    }

    [Fact]
    public async Task Transactions_stop_at_fifty_pages()
    {
        var (client, handler) = Client(HttpStatusCode.OK, """{"transactions":[],"continuation_key":"again"}""");

        var read = Ok(await client.TransactionsAsync("app", "s", "u", null, null, default));
        Assert.Equal((0, true), (read.Lines.Count, read.Truncated));
        Assert.Equal(EnableBankingClient.MaxPages, handler.Requests.Count);
    }

    [Fact]
    public async Task Psu_headers_are_sent_only_when_given()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("balances.json"));

        await client.BalancesAsync("app", "s", "uid", null, default);
        await client.BalancesAsync("app", "s", "uid", new PsuHeaders("203.0.113.7", "Mozilla/5.0"), default);
        await client.BalancesAsync("app", "s", "uid", new PsuHeaders(null, "Mozilla/5.0"), default);

        Assert.All(handler.Requests, r => Assert.True(r.Headers.Contains("Authorization")));
        Assert.False(handler.Requests[0].Headers.Contains("Psu-Ip-Address"));
        Assert.False(handler.Requests[0].Headers.Contains("Psu-User-Agent"));
        Assert.Equal(("203.0.113.7", "Mozilla/5.0"), (handler.Requests[1].Headers.GetValues("Psu-Ip-Address").Single(), handler.Requests[1].Headers.GetValues("Psu-User-Agent").Single()));
        Assert.False(handler.Requests[2].Headers.Contains("Psu-Ip-Address"));
    }

    [Fact]
    public async Task Every_call_carries_a_bearer_token_for_the_application()
    {
        var (client, handler) = Client(HttpStatusCode.OK, Vector("aspsps-fr.json"));
        await client.AspspsAsync("app-1", "FR", default);

        var auth = handler.Requests.Single().Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        var header = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!.Split('.')[0].Replace('-', '+').Replace('_', '/').PadRight(((auth.Parameter.Split('.')[0].Length + 3) / 4) * 4, '=')));
        Assert.Equal("app-1", JsonDocument.Parse(header).RootElement.GetProperty("kid").GetString());
    }

    [Fact]
    public async Task Delete_session_sends_a_delete()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}");

        Assert.True((await client.DeleteSessionAsync("app", "sess-1", default)).IsSuccess);
        Assert.Equal((HttpMethod.Delete, "https://api.enablebanking.com/sessions/sess-1"), (handler.Requests.Single().Method, handler.Requests.Single().Url.AbsoluteUri));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "error-expired-session.json", "enablebanking.EXPIRED_SESSION")]
    [InlineData(HttpStatusCode.TooManyRequests, "error-rate-limit.json", "enablebanking.ASPSP_RATE_LIMIT_EXCEEDED")]
    public async Task An_api_error_is_a_failure_named_after_it(HttpStatusCode status, string vector, string code)
    {
        var (client, _) = Client(status, Vector(vector));

        var result = await client.BalancesAsync("app", "s", "uid", null, default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(code, failure!.Code);
        Assert.DoesNotContain("uid", failure.Message);
    }

    [Fact]
    public async Task An_unnamed_error_is_the_status_and_an_unreadable_success_is_a_provider_error()
    {
        var (bad, _) = Client(HttpStatusCode.BadGateway, "<html>nope</html>");
        Assert.Equal("enablebanking.HTTP_502", FailureOf(await bad.AspspsAsync("app", "FR", default)));

        var (garbled, _) = Client(HttpStatusCode.OK, "<html>");
        Assert.Equal(BankingCodes.ProviderError, FailureOf(await garbled.AspspsAsync("app", "FR", default)));

        var (badAmount, _) = Client(HttpStatusCode.OK, """{"balances":[{"balance_type":"CLBD","balance_amount":{"currency":"EUR","amount":"abc"}}]}""");
        Assert.Equal(BankingCodes.ProviderError, FailureOf(await badAmount.BalancesAsync("app", "s", "u", null, default)));
    }

    [Fact]
    public async Task A_missing_or_unreadable_key_fails_without_a_request()
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{}", key: "not a key");
        Assert.Equal(BankingCodes.ProviderError, FailureOf(await client.AspspsAsync("app", "FR", default)));

        var none = new EnableBankingClient(
            new SafeHttpClient(new HttpMessageInvoker(handler), new OutboundOptions(), NullLogger<SafeHttpClient>.Instance), new BankingOptions(), new FixedClock(Now));
        Assert.Equal(BankingCodes.NotConfigured, FailureOf(await none.AspspsAsync("app", "FR", default)));
        Assert.Empty(handler.Requests);
    }

    private static string FailureOf<T>(UnambitiousFx.Functional.Result<T> result) where T : notnull
    {
        Assert.True(result.TryGetFailure(out var failure));
        return failure!.Code;
    }

    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}
