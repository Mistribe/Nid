using System.Text.Json.Serialization;

namespace Nid.Banking.Infrastructure.EnableBanking;

internal sealed record AspspsDto(List<AspspDto>? Aspsps = null);

internal sealed record AspspDto(string Name, string Country, string? Logo = null, int? MaximumConsentValidity = null);

internal sealed record StartAuthDto(AccessDto Access, AspspRefDto Aspsp, string State, string RedirectUrl, string PsuType);

internal sealed record AccessDto(string ValidUntil);

internal sealed record AspspRefDto(string Name, string Country);

internal sealed record StartAuthResponseDto(string Url);

internal sealed record CodeDto(string Code);

internal sealed record SessionDto(string SessionId, List<SessionAccountDto> Accounts, AccessDto Access);

/// <summary><c>details</c> is the account's description, <c>product</c> the bank's product name, <c>name</c> the holders' name.</summary>
internal sealed record SessionAccountDto(
    string IdentificationHash, string CashAccountType, string Currency, string? Uid = null, AccountIdDto? AccountId = null, string? Name = null,
    string? Details = null, string? Product = null, List<OtherIdDto>? AllAccountIds = null);

internal sealed record AccountIdDto(string? Iban = null, OtherIdDto? Other = null);

/// <summary>A non-IBAN identification; <c>CPAN</c> is a card's number, masked by the bank.</summary>
internal sealed record OtherIdDto(string? Identification = null, string? SchemeName = null);

internal sealed record BalancesDto(List<BalanceDto>? Balances = null);

internal sealed record BalanceDto(string BalanceType, AmountDto BalanceAmount, string? ReferenceDate = null);

internal sealed record TransactionsDto(List<TransactionDto>? Transactions = null, string? ContinuationKey = null);

internal sealed record TransactionDto(
    AmountDto TransactionAmount, string CreditDebitIndicator, string? EntryReference = null, string? Status = null, string? BookingDate = null,
    string? ValueDate = null, string? TransactionDate = null, List<string>? RemittanceInformation = null, PartyDto? Creditor = null, PartyDto? Debtor = null,
    BankCodeDto? BankTransactionCode = null);

internal sealed record PartyDto(string? Name = null);

internal sealed record BankCodeDto(string? Description = null);

internal sealed record AmountDto(string Currency, string Amount);

internal sealed record ErrorDto(string? Error = null);

/// <summary><c>GET /application</c>: only what the connection test shows; the name, countries and services are not read.</summary>
internal sealed record ApplicationDto(bool Active, List<string>? RedirectUrls = null);

// A missing or null required field is a JsonException (a provider error), never a null slipping through: optional ones have defaults.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AspspsDto))]
[JsonSerializable(typeof(StartAuthDto))]
[JsonSerializable(typeof(StartAuthResponseDto))]
[JsonSerializable(typeof(CodeDto))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(BalancesDto))]
[JsonSerializable(typeof(TransactionsDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(ApplicationDto))]
internal sealed partial class EbJson : JsonSerializerContext;
