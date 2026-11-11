using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nid.Banking.Contracts;
using UnambitiousFx.Myelin;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Infrastructure.EnableBanking;

/// <summary>
/// Enable Banking over <see cref="SafeHttpClient"/>. Never logs a body, a header, the token or a session id; a failure message names the
/// host, the HTTP status and the API's error name only.
/// </summary>
public sealed class EnableBankingClient(SafeHttpClient http, BankingOptions options, IClock clock) : IEnableBankingClient
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxPages = 50;
    private const string Base = "https://api.enablebanking.com";

    public async Task<Result<IReadOnlyList<Aspsp>>> AspspsAsync(string appId, string country, CancellationToken ct)
        => Map(await CallAsync<AspspsDto>(HttpMethod.Get, $"/aspsps?country={Uri.EscapeDataString(country)}", null, appId, null, ct),
            dto => (IReadOnlyList<Aspsp>)(dto.Aspsps ?? []).Select(a => new Aspsp(a.Name, a.Country, a.Logo, a.MaximumConsentValidity)).ToList());

    public async Task<Result<AuthStart>> StartAuthAsync(string appId, Aspsp aspsp, Instant validUntil, string state, string redirectUrl, CancellationToken ct)
    {
        var request = new StartAuthDto(
            new AccessDto(InstantPattern.ExtendedIso.Format(validUntil)), new AspspRefDto(aspsp.Name, aspsp.Country), state, redirectUrl, "personal");
        return Map(await CallAsync<StartAuthResponseDto>(HttpMethod.Post, "/auth", JsonSerializer.SerializeToUtf8Bytes(request, EbJson.Default.StartAuthDto), appId, null, ct),
            dto => new AuthStart(dto.Url));
    }

    public async Task<Result<AuthorizedSession>> CreateSessionAsync(string appId, string code, CancellationToken ct)
        => Map(await CallAsync<SessionDto>(HttpMethod.Post, "/sessions", JsonSerializer.SerializeToUtf8Bytes(new CodeDto(code), EbJson.Default.CodeDto), appId, null, ct), ToSession);

    /// <summary>The session is implied by the account uid on Enable Banking's side; <paramref name="sessionId"/> is not sent.</summary>
    public async Task<Result<IReadOnlyList<ProviderBalance>>> BalancesAsync(string appId, string sessionId, string accountUid, PsuHeaders? psu, CancellationToken ct)
    {
        return Map(await CallAsync<BalancesDto>(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(accountUid)}/balances", null, appId, PsuHeaderMap(psu), ct), ToBalances);
    }

    private static Dictionary<string, string> PsuHeaderMap(PsuHeaders? psu)
    {
        var headers = new Dictionary<string, string>();
        if (psu?.IpAddress is { Length: > 0 } ip)
            headers["Psu-Ip-Address"] = ip;
        if (psu?.UserAgent is { Length: > 0 } agent)
            headers["Psu-User-Agent"] = agent;
        return headers;
    }

    /// <summary>At most <see cref="MaxPages"/> pages: a bank that never stops sending a continuation key cannot hold a night.</summary>
    public async Task<Result<ProviderTransactions>> TransactionsAsync(
        string appId, string sessionId, string accountUid, LocalDate? dateFrom, PsuHeaders? psu, CancellationToken ct)
    {
        var headers = PsuHeaderMap(psu);
        var lines = new List<ProviderTransaction>();
        string? key = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var query = new List<string>();
            if (dateFrom is { } from)
                query.Add("date_from=" + LocalDatePattern.Iso.Format(from));
            if (key is not null)
                query.Add("continuation_key=" + Uri.EscapeDataString(key));
            var path = $"/accounts/{Uri.EscapeDataString(accountUid)}/transactions" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
            var result = await CallAsync<TransactionsDto>(HttpMethod.Get, path, null, appId, headers, ct);
            if (!result.TryGetValue(out var dto))
            {
                result.TryGetFailure(out var failure);
                return Result.Failure<ProviderTransactions>(failure!);
            }

            try
            {
                lines.AddRange((dto.Transactions ?? []).Select(ToTransaction).OfType<ProviderTransaction>());
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                return Fail<ProviderTransactions>(BankingCodes.ProviderError, "Enable Banking sent a response that could not be read.");
            }

            if (string.IsNullOrEmpty(key = dto.ContinuationKey))
                return Result.Success(new ProviderTransactions(lines, false));
        }

        return Result.Success(new ProviderTransactions(lines, true));
    }

    public async Task<Result> DeleteSessionAsync(string appId, string sessionId, CancellationToken ct)
    {
        var result = await CallAsync<object>(HttpMethod.Delete, $"/sessions/{Uri.EscapeDataString(sessionId)}", null, appId, null, ct, expectBody: false);
        return result.TryGetFailure(out var failure) ? Result.Failure(failure!) : Result.Success();
    }

    private static Result<T> Map<S, T>(Result<S> result, Func<S, T> map)
        where S : notnull where T : notnull
    {
        if (!result.TryGetValue(out var value))
        {
            result.TryGetFailure(out var failure);
            return Result.Failure<T>(failure!);
        }

        try
        {
            return Result.Success(map(value));
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            return Fail<T>(BankingCodes.ProviderError, "Enable Banking sent a response that could not be read.");
        }
    }

    private static AuthorizedSession ToSession(SessionDto dto) => new(
        dto.SessionId, Instant.FromDateTimeOffset(DateTimeOffset.Parse(dto.Access.ValidUntil, CultureInfo.InvariantCulture)),
        // An account without a uid cannot be read (blocked or closed at the bank): it is not offered.
        dto.Accounts.Where(a => a.Uid is not null)
            .Select(a => new SessionAccount(a.Uid!, a.IdentificationHash, a.AccountId?.Iban, a.Details ?? a.Name ?? "", a.CashAccountType, a.Currency))
            .ToList());

    private static IReadOnlyList<ProviderBalance> ToBalances(BalancesDto dto) => (dto.Balances ?? [])
        .Select(b => new ProviderBalance(
            b.BalanceType, decimal.Parse(b.BalanceAmount.Amount, NumberStyles.Number, CultureInfo.InvariantCulture), b.BalanceAmount.Currency,
            b.ReferenceDate is { } d ? LocalDatePattern.Iso.Parse(d).GetValueOrThrow() : null))
        .ToList();

    /// <summary>Only booked lines with a date; the counterparty is the creditor of a debit and the debtor of a credit.</summary>
    private static ProviderTransaction? ToTransaction(TransactionDto t)
    {
        if (t.Status is not "BOOK")
            return null;
        var date = t.BookingDate ?? t.ValueDate ?? t.TransactionDate;
        if (date is null)
            return null;
        var debit = t.CreditDebitIndicator == "DBIT";
        var amount = decimal.Parse(t.TransactionAmount.Amount, NumberStyles.Number, CultureInfo.InvariantCulture);
        var counterparty = (debit ? t.Creditor : t.Debtor)?.Name is { Length: > 0 } name ? name : null;
        var remittance = t.RemittanceInformation is { Count: > 0 } lines ? string.Join(' ', lines) : null;
        var label = remittance ?? counterparty ?? t.BankTransactionCode?.Description ?? "(sans libellé)";
        return new ProviderTransaction(
            string.IsNullOrWhiteSpace(t.EntryReference) ? null : t.EntryReference, LocalDatePattern.Iso.Parse(date).GetValueOrThrow(),
            t.ValueDate is { } v ? LocalDatePattern.Iso.Parse(v).GetValueOrThrow() : null,
            debit ? -Math.Abs(amount) : Math.Abs(amount), t.TransactionAmount.Currency, label, counterparty, remittance);
    }

    private async Task<Result<T>> CallAsync<T>(
        HttpMethod method, string path, byte[]? body, string appId, IReadOnlyDictionary<string, string>? extra, CancellationToken ct, bool expectBody = true)
        where T : notnull
    {
        if (string.IsNullOrWhiteSpace(options.PrivateKey))
            return Fail<T>(BankingCodes.NotConfigured, "No Enable Banking private key is configured.");
        string jwt;
        try
        {
            jwt = EnableBankingJwt.Create(appId, options.PrivateKey, clock.GetCurrentInstant());
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return Fail<T>(BankingCodes.ProviderError, "The Enable Banking private key could not be read.");
        }

        var headers = new Dictionary<string, string>(extra ?? new Dictionary<string, string>()) { ["Authorization"] = "Bearer " + jwt };
        try
        {
            var response = await http.SendOnceAsync(method, new Uri(Base + path), body, headers, MaxBytes, ct);
            if ((int)response.Status is < 200 or > 299)
                return Fail<T>(ErrorCode(response), $"api.enablebanking.com answered HTTP {(int)response.Status}.");
            if (!expectBody)
                return Result.Success<T>(default!);
            var dto = JsonSerializer.Deserialize(response.Body, EbJson.Default.GetTypeInfo(typeof(T))!) is T parsed ? parsed : default;
            return dto is null ? Fail<T>(BankingCodes.ProviderError, "Enable Banking sent an empty response.") : Result.Success(dto);
        }
        catch (JsonException)
        {
            return Fail<T>(BankingCodes.ProviderError, "Enable Banking sent a response that could not be read.");
        }
        catch (OutboundRefusedException refused)
        {
            return Fail<T>(BankingCodes.ProviderError, refused.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException
                                   || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail<T>(BankingCodes.ProviderError, $"api.enablebanking.com could not be reached ({e.GetType().Name}).");
        }
    }

    /// <summary><c>enablebanking.&lt;error&gt;</c> when the body names one (upper-case letters and underscores only: the name ends up in a code), else the status.</summary>
    private static string ErrorCode(OutboundResponse response)
    {
        try
        {
            var name = JsonSerializer.Deserialize(response.Body, EbJson.Default.ErrorDto)?.Error;
            if (name is { Length: > 0 and <= 64 } && name.All(c => c is >= 'A' and <= 'Z' or '_'))
                return EnableBankingErrors.Prefix + name;
        }
        catch (JsonException)
        {
        }

        return $"{EnableBankingErrors.Prefix}HTTP_{(int)response.Status}";
    }

    private static Result<T> Fail<T>(string code, string message) where T : notnull => Result.Failure<T>(new Failure(code, message, null));
}
