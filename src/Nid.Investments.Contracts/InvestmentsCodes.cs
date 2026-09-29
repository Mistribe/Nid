namespace Nid.Investments.Contracts;

/// <summary>2B1a spec §6.</summary>
public static class InvestmentsCodes
{
    public const string UnknownKind = "investments.unknown_kind";
    public const string UnknownCadence = "investments.unknown_cadence";
    public const string InvalidEnvelope = "investments.invalid_envelope";
    public const string EnvelopeClosed = "investments.envelope_closed";
    public const string EnvelopeOpen = "investments.envelope_open";
    public const string TransferSameEnvelope = "investments.transfer_same_envelope";
    public const string TransferCurrencyMismatch = "investments.transfer_currency_mismatch";
    public const string InvalidPeriod = "investments.invalid_period";
}
