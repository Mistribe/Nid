using System.Text.Json;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.Domain.Fx;

/// <summary><c>exchange/{yyyy}/{MM}/{dd}.json</c>: <c>{ "from": "USD", "to": { "EUR": 0.87, … } }</c>, no date inside (design §4.3).</summary>
public static class SubTrackerReader
{
    public static Result<FxReading> Read(byte[] json, LocalDate date)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            return Malformed("The file is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.String
                || !Currency.TryCreate(from.GetString(), out var baseCurrency)
                || !root.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.Object)
                return Malformed("Expected { \"from\": <currency>, \"to\": { <currency>: <number> } }.");

            var perBase = new Dictionary<Currency, decimal>();
            var rejected = new List<FxRejection>();
            foreach (var property in to.EnumerateObject())
            {
                if (!Currency.TryCreate(property.Name, out var quote))
                {
                    rejected.Add(new FxRejection(date, property.Name, 0m, null, FxCodes.InvalidCode));
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out var value) || value <= 0m)
                {
                    rejected.Add(new FxRejection(date, property.Name, 0m, null, FxCodes.InvalidValue));
                    continue;
                }

                perBase[quote] = value;
            }

            if (baseCurrency != Currency.Usd && !perBase.ContainsKey(Currency.Usd))
                return Malformed($"The file is based on {baseCurrency.Code} and gives no USD rate to rebase it.");

            var reading = FxRebase.ToUsd(date, baseCurrency, perBase, FxRateSource.SubTrackerData);
            return Result.Success(reading with { Rejected = [.. rejected, .. reading.Rejected] });
        }
    }

    private static Result<FxReading> Malformed(string message)
        => Result.Failure<FxReading>(new Failure(FxCodes.SourceMalformed, message, null));
}
