using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Contracts;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;
using Nid.Goals.Contracts;
using Nid.Host.Auth;
using Nid.Income.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.AspNetCore;
using UnambitiousFx.Functional.AspNetCore.Mappers;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Api;

/// <summary>
/// #250: how a failure becomes Problem Details (RFC 9457). Functional's mapper writes the body: a fixed <c>title</c> per
/// status, the failure's sentence in <c>detail</c>, its <c>code</c>, and for refused fields an <c>errors</c> list of
/// <c>{ detail, pointer, code }</c>. The status comes from the failure's type (not found, forbidden, conflict, bad
/// request…), unless its code is listed below; a plain <see cref="Failure"/> is a rule's refusal, 422.
/// A refusal that names no field is typed where it is made (<c>new ConflictFailure("…") { Code = … }</c>) and has no
/// entry here. One that names a field stays a field error, so that its <c>errors</c> list is written, and keeps its status
/// here (Investments: a closed holding, a snapshot date taken, a transfer leg, an income category, shares off 100 %, a holding
/// with no market exposure; Income: a one-off income dated after its source ended).
/// </summary>
public static class ProblemMapping
{
    /// <summary>The metadata a client reads next to the code (goals' free share, a statement's line, a series key, a person's uses, a split's two sums).</summary>
    private static readonly string[] Exposed = ["freeBp", "line", "key", "uses", "partsSum", "lineAmount"];

    /// <summary>Built once: the mapper holds no state but its table.</summary>
    public static IFailureHttpMapper Mapper { get; } = new ServiceCollection()
        .AddResultHttp(Configure)
        .BuildServiceProvider()
        .GetRequiredService<IFailureHttpMapper>();

    public static void Configure(ResultHttpOptions options)
    {
        options.ValidationErrors(ValidationErrorsFormat.Rfc9457);
        options.DefaultStatus(StatusCodes.Status422UnprocessableEntity);
        options.ExposeMetadata(Exposed);

        Map(options, StatusCodes.Status400BadRequest,
            LocalAccountCodes.LoginInvalid, LocalAccountCodes.PasswordTooShort, LocalAccountCodes.PasswordTooLong,
            LocalAccountCodes.PasswordTooCommon, LocalSignIn.CurrentPasswordWrong, LocalSignIn.InvalidCode,
            SeriesCodes.InvalidKey, // names its field (s)
            InvestmentsCodes.InvalidIncomeCategory, InvestmentsCodes.InvalidContributionCategory, InvestmentsCodes.TransferLeg, BreakdownCodes.Not100, BreakdownCodes.NotMarket,
            DocumentsCodes.InvalidField, DocumentsCodes.InvalidRole, DocumentsCodes.UnknownTargetKind);
        Map(options, StatusCodes.Status400BadRequest, [.. ImportCodes.BadRequests]);

        Map(options, StatusCodes.Status403Forbidden, AdminCodes.Required, BrowserOnlyCodes.Required);

        Map(options, StatusCodes.Status404NotFound,
            LocalAccountCodes.SetupClosed, SeriesCodes.NotFound, CoreFactCodes.FactNotFound,
            DocumentsCodes.TargetNotFound, DocumentsCodes.ContentMissing,
            BankingCodes.ConnectionNotFound, BankingCodes.AccountNotFound);

        Map(options, StatusCodes.Status409Conflict,
            CoreFactCodes.FactRetired, CoreFactCodes.OpeningExists, CoreFactCodes.ExternalRefTaken, CoreFactCodes.FactConflict,
            InvestmentsCodes.HoldingClosed, InvestmentsCodes.SnapshotExists,
            IncomeCodes.SourceEnded,
            ChildcareCodes.StatementValidated, // a week of a validated month names its field (monday)
            GoalsCodes.ShareExceeded, // a goal's funding line names its field (funding[i].shareBp)
            DocumentsCodes.AlreadyLinked);
        Map(options, StatusCodes.Status409Conflict, [.. ImportCodes.Conflicts]);

        Map(options, StatusCodes.Status413PayloadTooLarge, DocumentsCodes.TooLarge, BankingCodes.FileTooLarge);
        Map(options, StatusCodes.Status415UnsupportedMediaType, DocumentsCodes.UnsupportedType);
        Map(options, StatusCodes.Status429TooManyRequests, NotificationCodes.TestThrottled);
        Map(options, StatusCodes.Status500InternalServerError, FailureCodes.Failure, "core.access_violation");
    }

    private static void Map(ResultHttpOptions options, int status, params string[] codes)
    {
        foreach (var code in codes)
            options.MapCode(code, status);
    }

    /// <summary>
    /// A rule's refusal that names its fields, as the <see cref="ValidationFailure"/> the mapper writes an <c>errors</c>
    /// list for: the fields of <see cref="FieldErrors"/>, or the one a rule names in its <c>field</c> metadata (2C2a spec §6).
    /// Its code, sentence and other metadata are kept; a typed failure (not found, forbidden) is no field's and stays as it is.
    /// </summary>
    public static Failure Located(Failure failure)
    {
        if (failure.GetType() != typeof(Failure))
            return failure;
        var errors = FieldErrors.Of(failure)
            ?? (failure.Metadata.TryGetValue("field", out var field) && field is string path
                ? [new FieldError(path, failure.Code, failure.Message)]
                : null);
        if (errors is null)
            return failure;
        var extra = failure.Metadata.Where(m => m.Key is not ("errors" or "field")).ToDictionary(m => m.Key, m => m.Value);
        return new ValidationFailure([.. errors.Select(e => new ValidationError(e.Path, e.Message, e.Code))], extra)
        {
            Code = failure.Code,
            Message = failure.Message,
        };
    }
}
