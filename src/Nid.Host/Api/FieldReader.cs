using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using static Nid.Host.Api.SubscriptionEndpoints;
using Duration = Nid.Core.Contracts.Duration;
using IResult = Microsoft.AspNetCore.Http.IResult;

namespace Nid.Host.Api;

/// <summary>
/// Reads a body field by field and keeps every refusal with its path, so one answer names all of them (1E2a design §2.1).
/// A null result always comes with an error.
/// </summary>
internal sealed class FieldReader
{
    private readonly List<FieldError> errors = [];

    public IReadOnlyList<FieldError> Errors => errors;
    public bool Ok => errors.Count == 0;

    public void Add(string path, string code, string message) => errors.Add(new FieldError(path, code, message));

    public LocalDate? Date(string? value, string path, bool required = true)
    {
        if (value is null)
        {
            if (required) Add(path, "request.required", "This date is required.");
            return null;
        }
        if (LocalDatePattern.Iso.Parse(value) is { Success: true } parsed)
            return parsed.Value;
        Add(path, "request.date", $"'{value}' is not an ISO date (yyyy-mm-dd).");
        return null;
    }

    public LocalTime? Time(string? value, string path)
    {
        if (value is null)
        {
            Add(path, "request.required", "This time is required.");
            return null;
        }
        if (NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(value) is { Success: true } parsed)
            return parsed.Value;
        Add(path, "request.time", $"'{value}' is not a time (HH:mm).");
        return null;
    }

    public Money? Money(MoneyBody? body, string path)
    {
        if (body is null) { Add(path, "request.required", "An amount is required."); return null; }
        if (Core.Contracts.Money.TryParse(body.Amount, body.Currency, out var money)) return money;
        if (!Currency.TryCreate(body.Currency ?? "", out _))
            Add(path + ".currency", "request.currency", "A currency is a three-letter ISO 4217 code.");
        else
            Add(path + ".amount", "request.amount", "An amount is a decimal string such as \"12.99\" (at most four decimals).");
        return null;
    }

    public Duration? Duration(DurationBody? body, string path, bool required = true)
    {
        if (body is null)
        {
            if (required) Add(path, "request.required", "A duration is required.");
            return null;
        }
        var unitOk = Enum.TryParse<DurationUnit>(body.Unit, ignoreCase: true, out var unit) && Enum.IsDefined(unit);
        if (!unitOk) Add(path + ".unit", "request.unit", "A unit is day, week, month or year.");
        if (body.Count <= 0) Add(path + ".count", "request.count", "A count is positive.");
        return unitOk && body.Count > 0 && Core.Contracts.Duration.Create(body.Count, unit).TryGetValue(out var duration) ? duration : null;
    }

    public Payer? Payer(PayerBody? body, string path)
    {
        if (body is null) { Add(path, "request.required", "A payer is required."); return null; }
        if (!Enum.TryParse<PayerType>(body.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            Add(path + ".type", "request.payer", "A payer type is person, household or external.");
            return null;
        }
        if (type == PayerType.Person && body.PersonId is null)
        {
            Add(path + ".personId", "request.required", "A person pays: say who.");
            return null;
        }
        return new Payer(type, body.PersonId is { } p ? new PersonId(p) : null);
    }

    public Visibility? Visibility(string? value, string path)
    {
        if (Enum.TryParse<Visibility>(value, ignoreCase: true, out var visibility) && Enum.IsDefined(visibility)) return visibility;
        Add(path, "request.visibility", "Visibility is shared or private.");
        return null;
    }

    /// <summary>#294: <c>view</c> or <c>edit</c>; an omitted level is <c>view</c>, today's sharing.</summary>
    public ShareLevel? ShareLevel(string? value, string path)
    {
        if (value is null) return Nid.Core.Contracts.ShareLevel.View;
        if (Enum.TryParse<ShareLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level)) return level;
        Add(path, "request.share_level", "The sharing level is view or edit.");
        return null;
    }

    public string? Text(string? value, string path)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        Add(path, "request.required", "This field is required.");
        return null;
    }

    public decimal? Decimal(string? value, string path)
    {
        if (value is null) return null;
        if (decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return amount;
        Add(path, "request.amount", "An amount is a decimal string such as \"12.99\".");
        return null;
    }

    public IResult Problem() => Result.Failure<bool>(FieldErrors.Shape(errors)).ToHttp(_ => new { });
}
