namespace Nid.Core.Contracts;

/// <summary>ISO 4217 currency code: three uppercase ASCII letters.</summary>
public readonly record struct Currency
{
    public static readonly Currency Eur = new("EUR");
    public static readonly Currency Usd = new("USD");

    private Currency(string code) => Code = code;

    public string Code { get; }

    public static bool TryCreate(string? code, out Currency currency)
    {
        if (code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z'))
        {
            currency = new Currency(code);
            return true;
        }

        currency = default;
        return false;
    }

    public override string ToString() => Code;
}
