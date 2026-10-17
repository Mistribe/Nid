using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.RealEstate.Application;

/// <summary>
/// Spec 4D2 §3: `{ rows[], makeReference? }` on a loan, the bank's table as JSON rows; subject key: the loan (one pending table per
/// loan, a new one supersedes it). Checked by 3F2's rules (<see cref="BankCheck.Rows"/>), refused at `payload.rows[k].column`; a
/// table that diverges from the calculation is warned (`schedule_mismatch`), not refused. `makeReference` is an approval's edit only
/// (D4): a submitter's is dropped, and absent it is true. The same rows already stored with the bank as the reference: « conforme ».
/// </summary>
public sealed class AmortizationProposal(ILoanStore loans, IItemRegistry items, IInvoker invoker) : IProposalHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Module => "realestate";
    public string Kind => "realestate.import_amortization";
    public int PayloadVersion => 1;

    private sealed record Body(List<RowBody?>? Rows, JsonElement? MakeReference);

    private sealed record RowBody(string? DueDate, string? Payment, string? Interest, string? Principal, string? Insurance, string? Crd);

    private sealed record Parsed(IReadOnlyList<BankRow> Rows, bool? MakeReference);

    public async Task<Result<ProposalCheck>> ValidateAsync(ItemId target, JsonElement payload, bool edited, CancellationToken ct)
    {
        // `makeReference` is an approval's edit only: a submission's is ignored, never refused.
        var parsed = Parse(payload, readMakeReference: edited);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<ProposalCheck>(FailureOf(parsed));
        var loaded = await LoanCheck.LoadAsync(loans, items, target, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<ProposalCheck>(FailureOf(loaded));
        var (terms, computed) = Computed(found);
        if (BankCheck.Rows(p!.Rows, terms, computed).TryGetFailure(out var refused))
            return Result.Failure<ProposalCheck>(AtPayload(refused!));

        // D6: only the table already stored AND already the reference is « conforme »; on the calculation it is proposed, to become it.
        var stored = LoanReading.BankRows(found.Loan);
        if (found.Loan.Source == ScheduleSource.Bank && stored.SequenceEqual(p.Rows))
            return Result.Success(new ProposalCheck(Key(target), [], Normalized(p),
                JsonSerializer.SerializeToElement(new { expected = Span(stored), given = Span(p.Rows) }, Json)));

        var summary = BankSchedule.Diff(computed, p.Rows).Summary;
        List<ProposalWarning> warnings = summary.DivergesOn is { } on
            ? [new ProposalWarning(LoanWarnings.ScheduleMismatch, "warning", Mismatch(summary, on), $"payload.rows[{RankOf(p.Rows, on)}]")]
            : [];
        return Result.Success(new ProposalCheck(Key(target), warnings, Normalized(p), null, JsonSerializer.SerializeToElement(SummaryOf(summary), Json)));
    }

    public async Task<JsonElement> PreviewAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var loaded = await LoanCheck.LoadAsync(loans, items, target, ct);
        if (!Parse(payload, readMakeReference: true).TryGetValue(out var p) || !loaded.TryGetValue(out var found))
            return JsonSerializer.SerializeToElement(new
            {
                currency = (string?)null, summary = (object?)null, reference = (object?)null,
                first = Array.Empty<object>(), last = Array.Empty<object>(), count = 0, range = (object?)null,
            }, Json);
        var (terms, computed) = Computed(found);
        var rows = p!.Rows;
        var current = found.Loan.Source;
        return JsonSerializer.SerializeToElement(new
        {
            currency = terms.Currency.Code,
            summary = SummaryOf(BankSchedule.Diff(computed, rows).Summary),
            reference = new { current = LoanWire.Of(current), proposed = LoanWire.Of((p.MakeReference ?? true) ? ScheduleSource.Bank : current) },
            first = rows.Take(3).Select(RowNode),
            last = rows.Skip(Math.Max(3, rows.Count - 3)).Select(RowNode),
            count = rows.Count,
            range = new { from = Iso(rows[0].DueDate), to = Iso(rows[^1].DueDate) },
        }, Json);
    }

    public async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(ItemId target, JsonElement payload, CancellationToken ct)
    {
        var parsed = Parse(payload, readMakeReference: true);
        if (!parsed.TryGetValue(out var p))
            return Result.Failure<IReadOnlyList<Guid>>(FailureOf(parsed));
        // D4: absent, the table becomes the reference — the approval's default, and a direct write's.
        var stored = await invoker.InvokeAsync(new ReplaceBankRowsCommand(target, p!.Rows, p.MakeReference ?? true), ct);
        return stored.TryGetValue(out var id)
            ? Result.Success((IReadOnlyList<Guid>)[id.Value])
            : Result.Failure<IReadOnlyList<Guid>>(AtPayload(FailureOf(stored)));
    }

    /// <summary>The rows' own paths (`rows[k].column`, `rows`) as the payload's; anything unlocated at `payload`.</summary>
    private static Failure AtPayload(Failure failure)
        => FieldErrors.Of(failure) is { Count: > 0 } errors
            ? FieldErrors.Many(failure.Code, failure.Message, [.. errors.Select(e => e with { Path = $"payload.{e.Path}" })])
            : FieldErrors.At(failure, "payload");

    private static (LoanTerms Terms, LoanSchedule Schedule) Computed((StoredLoan Loan, ItemAccess Item) found)
    {
        var terms = LoanReading.Terms(LoanViews.CurrencyOf(found.Item), found.Loan.Contract, found.Loan.Fees, found.Loan.Repayments);
        return (terms, LoanReading.Computed(terms));
    }

    private static string Key(ItemId loan) => loan.Value.ToString();

    private static object Span(IReadOnlyList<BankRow> rows) => new { count = rows.Count, from = Iso(rows[0].DueDate), to = Iso(rows[^1].DueDate) };

    /// <summary>`ScheduleComparisonView`'s names, so the web reads it as the loan page's comparison.</summary>
    private static object SummaryOf(ScheduleDiffSummary s) => new
    {
        maxGap = LoanReading.Amount(s.MaxGap), maxGapOn = Iso(s.MaxGapOn), divergesOn = Iso(s.DivergesOn),
        interestGap = LoanReading.Amount(s.InterestGap), rowsBeyondRounding = s.RowsBeyondRounding,
    };

    private static string Mismatch(ScheduleDiffSummary s, LocalDate on)
        => $"The bank's table diverges from the calculation from {Iso(on)} (a remaining balance gap above {LoanReading.Amount(BankSchedule.Divergence)}); "
           + $"largest gap {LoanReading.Amount(s.MaxGap)} on {Iso(s.MaxGapOn)}, cumulated interest gap {LoanReading.Amount(s.InterestGap)}, "
           + $"{s.RowsBeyondRounding} rows beyond {LoanReading.Amount(BankSchedule.Rounding)}.";

    private static int RankOf(IReadOnlyList<BankRow> rows, LocalDate on) => rows.Select((r, k) => (r.DueDate, k)).First(x => x.DueDate == on).k;

    /// <summary>A row as stored in the payload and shown in the preview (`BankRowView`'s names); no insurance when the table gives none.</summary>
    private static JsonObject RowNode(BankRow r)
    {
        var row = new JsonObject
        {
            ["dueDate"] = Iso(r.DueDate), ["payment"] = LoanReading.Amount(r.Payment), ["interest"] = LoanReading.Amount(r.Interest),
            ["principal"] = LoanReading.Amount(r.Principal),
        };
        if (r.Insurance is { } insurance)
            row["insurance"] = LoanReading.Amount(insurance);
        row["crd"] = LoanReading.Amount(r.Crd);
        return row;
    }

    /// <summary>The known fields only: what is fingerprinted and stored.</summary>
    private static JsonElement Normalized(Parsed p)
    {
        var body = new JsonObject { ["rows"] = new JsonArray(p.Rows.Select(r => (JsonNode?)RowNode(r)).ToArray()) };
        if (p.MakeReference is { } make)
            body["makeReference"] = make;
        return JsonSerializer.SerializeToElement(body);
    }

    private static Result<Parsed> Parse(JsonElement payload, bool readMakeReference)
    {
        Body? body;
        try
        {
            body = payload.Deserialize<Body>(Json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) // an amount sent as a number, rows not a list
        {
            body = null;
        }
        if (body is null)
            return Shape([new FieldError("payload.rows", FieldErrors.ShapeCode,
                "A list of rows { dueDate, payment, interest, principal, insurance?, crd }, each value a string.")]);
        var raw = body.Rows ?? [];
        if (raw.Count is 0 or > LoanScheduleBuilder.MaxRows)
            return Shape([new FieldError("payload.rows", FieldErrors.ShapeCode,
                $"1 to {LoanScheduleBuilder.MaxRows} rows: the whole table, every row as printed.")]);

        var errors = new List<FieldError>();
        decimal? Amount(string? text, string path)
        {
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x))
                return x;
            errors.Add(new FieldError(path, FieldErrors.ShapeCode, "A decimal amount of 0 or more with a point (e.g. '1159.92')."));
            return null;
        }

        var rows = new List<BankRow>();
        for (var k = 0; k < raw.Count; k++)
        {
            var at = $"payload.rows[{k}]";
            if (raw[k] is not { } r)
            {
                errors.Add(new FieldError(at, FieldErrors.ShapeCode, "A row is an object."));
                continue;
            }
            var before = errors.Count;
            LocalDate due = default;
            if (r.DueDate is not { } date || LocalDatePattern.Iso.Parse(date) is not { Success: true } parsedDate)
                errors.Add(new FieldError($"{at}.dueDate", FieldErrors.ShapeCode, "An ISO date (yyyy-mm-dd) is required."));
            else
                due = parsedDate.Value;
            var payment = Amount(r.Payment, $"{at}.payment");
            var interest = Amount(r.Interest, $"{at}.interest");
            var principal = Amount(r.Principal, $"{at}.principal");
            var insurance = r.Insurance is null ? null : Amount(r.Insurance, $"{at}.insurance");
            var crd = Amount(r.Crd, $"{at}.crd");
            if (errors.Count == before)
                rows.Add(new BankRow(due, payment!.Value, interest!.Value, principal!.Value, insurance, crd!.Value));
        }
        // A garbage table answers its first cells, not thousands (as a pasted one, 3F2).
        if (errors.Count > 0)
            return Shape([.. errors.Take(BankCheck.ErrorMax)]);
        // Read leniently: ignored whatever its type unless an edit, where only true/false are accepted.
        bool? make = null;
        if (readMakeReference && body.MakeReference is { ValueKind: not JsonValueKind.Null } m)
        {
            if (m.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Shape([new FieldError("payload.makeReference", FieldErrors.ShapeCode, "true or false.")]);
            make = m.GetBoolean();
        }
        return Result.Success(new Parsed(rows, make));
    }

    private static Result<Parsed> Shape(IReadOnlyList<FieldError> errors) => Result.Failure<Parsed>(FieldErrors.Shape(errors));

    private static Failure FailureOf<T>(Result<T> result) where T : notnull
        => result.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException();

    private static string Iso(LocalDate date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Iso(LocalDate? date) => date is { } d ? Iso(d) : null;
}
