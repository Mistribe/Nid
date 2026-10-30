using System.Text.Json.Serialization;

namespace Nid.Banking.Contracts;

/// <summary>
/// A bank account of <c>nid.data</c> (bank sync D24): <c>ref</c> is its file key, the references name a <c>bank_accounts</c> ref, an
/// <c>envelopes</c> id and a <c>persons</c> ref. Never a connection, a session or a balance.
/// </summary>
public sealed record BankAccountRecord
{
    public required string Ref { get; init; }
    public required string Name { get; init; }

    [JsonPropertyName("iban_last4")] // the snake case policy writes no underscore before digits
    public string? IbanLast4 { get; init; }

    public required string Kind { get; init; }
    public required string Currency { get; init; }
    public string? EnvelopeRef { get; init; }
    public string? CardOfRef { get; init; }
    public bool AutoApplyBalance { get; init; }
    public bool Ignored { get; init; }
    public required string OwnerRef { get; init; }
}

/// <summary>A booked line of a <c>bank_accounts</c> account (D24), signed; dates stay strings, the section parses them. Amounts read as decimals, never doubles.</summary>
public sealed record BankTransactionRecord
{
    public required string AccountRef { get; init; }
    public required string ProviderRef { get; init; }
    public required string Fingerprint { get; init; }
    public required string BookedOn { get; init; }
    public string? ValueOn { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
    public required string Label { get; init; }
    public string? Counterparty { get; init; }
    public string? Remittance { get; init; }
    public required string FetchedVia { get; init; }
}
