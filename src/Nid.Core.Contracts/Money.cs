using System.Globalization;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Contracts;

/// <summary>
/// An amount in its original currency. PostgreSQL <c>numeric(19,4)</c> rounds silently beyond four
/// decimals, so this type refuses them instead of letting the database change the value.
/// </summary>
public readonly record struct Money
{
    public const int MaxDecimals = 4;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    /// <summary>Wire form of the amount: invariant culture, no thousands separator.</summary>
    public string AmountText => Amount.ToString(CultureInfo.InvariantCulture);

    public static Result<Money> Create(decimal amount, Currency currency)
    {
        if (decimal.Round(amount, MaxDecimals) != amount)
            return Result.Failure<Money>(new Failure("core.money_scale", $"An amount has at most {MaxDecimals} decimals.", null));

        return Result.Success(new Money(amount, currency));
    }

    public static bool TryParse(string? amountText, string? currencyCode, out Money money)
    {
        money = default;
        if (!Currency.TryCreate(currencyCode, out var currency))
            return false;
        if (!decimal.TryParse(amountText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
            return false;

        return Create(amount, currency).TryGetValue(out money);
    }
}
