using System.ComponentModel;
using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Api;

/// <summary>Childcare spec §5: the arrangement reads and writes. Rates are decimal strings, times <c>HH:mm</c>.</summary>
public static class ChildcareEndpoints
{
    public static IEndpointRouteBuilder MapChildcareEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/childcare").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("", async (string? scope, bool? closed, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListChildcareQuery(ParseScope(scope), closed ?? false), ct)).ToHttp(list => list))
            .Returns<ChildcareList>();

        api.MapGet("/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new GetChildcareQuery(new ItemId(id)), ct)).ToHttp(detail => detail))
            .Returns<ChildcareDetail>();

        api.MapPost("", async (CreateChildcareBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(reader);
                return command is null
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(command, ct)).ToHttp(id => new IdResponse(id.Value), StatusCodes.Status201Created);
            })
            .Returns<IdResponse>(StatusCodes.Status201Created);

        api.MapPut("/{id:guid}", async (Guid id, EditChildcareBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), reader);
                return command is null ? reader.Problem() : (await invoker.InvokeAsync(command, ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/revisions", async (Guid id, ReviseChildcareBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var terms = ChildcareBodies.Terms(reader, body.Terms);
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new ReviseChildcareCommand(new ItemId(id), terms!), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPut("/{id:guid}/revisions/{from}", async (Guid id, string from, UnpaidAbsencesBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var start = reader.Date(from, "from");
                var reasons = ChildcareBodies.Absences(reader, body.UnpaidAbsences, "unpaidAbsences");
                if (body.UnpaidAbsences is null) reader.Add("unpaidAbsences", "request.required", "The absence reasons deducted from the salary are required, [] for none.");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SetUnpaidAbsencesCommand(new ItemId(id), start!.Value, reasons!), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPut("/{id:guid}/gaps", async (Guid id, GapsBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var gaps = ChildcareBodies.Gaps(reader, body.Gaps);
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new ReplaceGapsCommand(new ItemId(id), gaps), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPut("/{id:guid}/week-overrides/{monday}", async (Guid id, string monday, WeekOverrideBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var date = reader.Date(monday, "monday");
                if (body.WeekType is null) reader.Add("weekType", "request.required", "The week type is required.");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new SetWeekOverrideCommand(new ItemId(id), date!.Value, body.WeekType!.Value), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/close", async (Guid id, CloseChildcareBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var on = reader.Date(body.On, "on");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new CloseChildcareCommand(new ItemId(id), on!.Value), ct)).ToHttp(x => new IdResponse(x.Value));
            })
            .Returns<IdResponse>();

        api.MapPost("/{id:guid}/reopen", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ReopenChildcareCommand(new ItemId(id)), ct)).ToHttp(x => new IdResponse(x.Value)))
            .Returns<IdResponse>();

        api.MapGet("/{id:guid}/deletion", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ChildcareDeletionQuery(new ItemId(id)), ct)).ToHttp(preview => preview))
            .Returns<ItemDeletionPreview>();

        api.MapDelete("/{id:guid}", async (Guid id, uint? version, IInvoker invoker, CancellationToken ct) =>
            {
                // A missing version answers 422 at `version`: it would otherwise bind to 0 and pass for a conflict.
                if (version is null)
                {
                    var reader = new FieldReader();
                    reader.Add("version", "request.required", "The version read with it is required.");
                    return reader.Problem();
                }

                var result = await invoker.InvokeAsync(new DeleteChildcareCommand(new ItemId(id), version.Value), ct);
                return result.TryGetValue(out _) ? Results.NoContent() : result.ToHttp(x => x);
            })
            .ReturnsNoContent();

        api.MapGet("/{id:guid}/statements", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListStatementsQuery(new ItemId(id)), ct)).ToHttp(list => list))
            .Returns<ChildcareStatementList>();

        // #411: the year's validated months for the tax return; no year is the year just ended.
        api.MapGet("/{id:guid}/summary", async (Guid id, int? year, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                if (year is < 2000 or > 2200) reader.Add("year", "request.invalid", "A year is 2000 to 2200.");
                return !reader.Ok ? reader.Problem() : (await invoker.InvokeAsync(new GetChildcareYearQuery(new ItemId(id), year), ct)).ToHttp(view => view);
            })
            .Returns<ChildcareYearView>();

        api.MapGet("/{id:guid}/statements/{month}", async (Guid id, string month, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                return ChildcareBodies.Month(reader, month) is { } m
                    ? (await invoker.InvokeAsync(new GetStatementQuery(new ItemId(id), m), ct)).ToHttp(view => view)
                    : reader.Problem();
            })
            .Returns<ChildcareStatementView>();

        api.MapPut("/{id:guid}/statements/{month}", async (Guid id, string month, StatementBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var command = body.ToCommand(new ItemId(id), ChildcareBodies.Month(reader, month), reader);
                return command is null ? reader.Problem() : (await invoker.InvokeAsync(command, ct)).ToHttp(view => view);
            })
            .Returns<ChildcareStatementView>();

        api.MapPost("/{id:guid}/statements/{month}/validate", async (Guid id, string month, VersionBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var m = ChildcareBodies.Month(reader, month);
                if (body.Version is null) reader.Add("version", "request.required", "The version read with the statement is required.");
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new ValidateStatementCommand(new ItemId(id), m!.Value, body.Version!.Value), ct)).ToHttp(view => view);
            })
            .Returns<ChildcareStatementView>();

        api.MapPost("/{id:guid}/statements/{month}/reopen", async (Guid id, string month, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                return ChildcareBodies.Month(reader, month) is { } m
                    ? (await invoker.InvokeAsync(new ReopenStatementCommand(new ItemId(id), m), ct)).ToHttp(view => view)
                    : reader.Problem();
            })
            .Returns<ChildcareStatementView>();

        api.MapDelete("/{id:guid}/statements/{month}", async (Guid id, string month, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                return ChildcareBodies.Month(reader, month) is { } m
                    ? (await invoker.InvokeAsync(new DiscardStatementCommand(new ItemId(id), m), ct)).ToHttp(view => view)
                    : reader.Problem();
            })
            .Returns<ChildcareStatementView>();

        return routes;
    }

    /// <summary>A day as entered; times <c>HH:mm</c> on a present day, <c>absence</c> snake_case (<c>child_sick</c>) on an absent planned one.</summary>
    public sealed record StatementDayBody(string? Date, bool? Present, string? Arrival, string? Departure, bool? Lunch, string? Absence, bool? Snack = null);

    /// <summary>Decision 29: the month's care days, its total hours as a decimal string, and the reason of the planned days left over.</summary>
    public sealed record StatementSummaryBody(int? CareDays, string? Hours, string? Absence);

    /// <summary>Decision 30: the declaration's fields entered by hand, null to keep the month's; amounts and hours as decimal strings.</summary>
    public sealed record DeclaredBody(int? NormalHours, int? ActivityDays, int? PaidLeaveDays, string? ComplementaryHours, string? OvertimeHours, string? NetPay, string? Upkeep, string? Meals);

    /// <summary>
    /// Every day of the month, the month's mileage and CMG as decimal strings, and the <c>version</c> read (0 when nothing was stored).
    /// <c>declared</c>: the fields entered by hand; <c>summary</c>: replaces the days; <c>contributions</c>: #30, those the Pajemploi summary
    /// leaves to the household, null while not known.
    /// </summary>
    public sealed record StatementBody(IReadOnlyList<StatementDayBody>? Days, string? Mileage, string? Cmg, uint? Version, DeclaredBody? Declared = null, StatementSummaryBody? Summary = null,
        string? Contributions = null)
    {
        internal SaveStatementCommand? ToCommand(ItemId id, YearMonth? month, FieldReader reader)
        {
            var days = new List<DayInput>();
            foreach (var (day, i) in (Days ?? []).Select((d, i) => (d, i)))
            {
                var date = reader.Date(day?.Date, $"days[{i}].date");
                var present = day?.Present ?? false;
                var arrival = present ? reader.Time(day!.Arrival, $"days[{i}].arrival") : null;
                var departure = present ? reader.Time(day!.Departure, $"days[{i}].departure") : null;
                AbsenceReason? absence = null;
                if (day?.Absence is { } name && (absence = AbsenceCodes.TryParse(name)) is null)
                    reader.Add($"days[{i}].absence", "request.invalid", "An absence is family, parent_rtt, child_sick, carer_absent, carer_training, holiday or paid_leave.");
                if (date is { } d) days.Add(new DayInput(d, present, arrival, departure, day?.Lunch ?? false, absence, day?.Snack ?? false));
            }
            var mileage = reader.Decimal(Mileage, "mileage") ?? 0m;
            var cmg = reader.Decimal(Cmg, "cmg");
            var contributions = reader.Decimal(Contributions, "contributions");
            var declared = Declared is { } e
                ? new DeclaredFigures(reader.Decimal(e.NetPay, "declared.netPay"), reader.Decimal(e.Upkeep, "declared.upkeep"), reader.Decimal(e.Meals, "declared.meals"),
                    e.NormalHours, e.ActivityDays, e.PaidLeaveDays, reader.Decimal(e.ComplementaryHours, "declared.complementaryHours"), reader.Decimal(e.OvertimeHours, "declared.overtimeHours"))
                : null;
            MonthSummaryInput? summary = null;
            if (Summary is { } s)
            {
                AbsenceReason? reason = null;
                if (s.Absence is { } name && (reason = AbsenceCodes.TryParse(name)) is null)
                    reader.Add("summary.absence", "request.invalid", "An absence is family, parent_rtt, child_sick, carer_absent, carer_training, holiday or paid_leave.");
                if (s.CareDays is null) reader.Add("summary.careDays", "request.required", "The care days are required.");
                var hours = reader.Decimal(s.Hours, "summary.hours");
                if (hours is null) reader.Add("summary.hours", "request.required", "The hours are required.");
                if (reader.Ok) summary = new MonthSummaryInput(s.CareDays!.Value, hours!.Value, reason);
            }
            if (Version is null) reader.Add("version", "request.required", "The version read with the statement is required.");
            return reader.Ok ? new SaveStatementCommand(id, month!.Value, days, mileage, cmg, Version!.Value, declared, summary, contributions) : null;
        }
    }

    public sealed record VersionBody(uint? Version);

    public sealed record CarerBody(string? Name, string? Pajemploi, string? Approval);

    /// <summary>ISO day (1 = Monday), times <c>HH:mm</c>.</summary>
    public sealed record WeekdaySlotBody(int? Day, string? Arrival, string? Departure, bool? Lunch, bool? Snack = null);

    /// <summary>Rates as decimal strings; <c>overtimePct</c> in percent (10 = 10 %).</summary>
    public sealed record TermsBody(
        string? From, string? NetHourlyRate, string? GrossHourlyRate, string? OvertimePct, string? UpkeepPer9h, string? LunchPrice,
        IReadOnlyList<WeekTypeBody>? WeekTypes, string? SnackPrice = null, string? LunchProvider = null, string? SnackProvider = null, IReadOnlyList<string>? UnpaidAbsences = null);

    /// <summary>A named weekly pattern and the number of weeks of the contract year it covers.</summary>
    public sealed record WeekTypeBody(string? Name, int? Weeks, IReadOnlyList<WeekdaySlotBody>? Schedule);

    /// <summary>The week type (its 0-based rank in the revision) a week follows instead of the rotation's.</summary>
    public sealed record WeekOverrideBody([property: Description("Rank of the week type in the revision, 0 for the first.")] int? WeekType);

    public sealed record CareGapBody(string? Monday, int? Weeks);

    public sealed record CreateChildcareBody(
        string? Name, Guid? ChildId, CarerBody? Carer, TermsBody? Terms, IReadOnlyList<CareGapBody>? Gaps, string? Visibility, IReadOnlyList<OwnerBody>? Owners,
        string? ShareLevel = null)
    {
        internal CreateChildcareCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            if (ChildId is null) reader.Add("childId", "request.required", "The child is required.");
            var carer = ChildcareBodies.Carer(reader, Carer);
            var terms = ChildcareBodies.Terms(reader, Terms);
            var gaps = ChildcareBodies.Gaps(reader, Gaps);
            var level = reader.ShareLevel(ShareLevel, "shareLevel");
            return reader.Ok
                ? new CreateChildcareCommand(Name ?? "", new PersonId(ChildId!.Value), carer!, terms!, gaps, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList(), level!.Value)
                : null;
        }
    }

    /// <summary>Corrects the arrangement and its current terms (whose start stays), with the <c>version</c> read.</summary>
    public sealed record EditChildcareBody(string? Name, CarerBody? Carer, TermsBody? Terms, uint? Version)
    {
        internal EditChildcareCommand? ToCommand(ItemId id, FieldReader reader)
        {
            var carer = ChildcareBodies.Carer(reader, Carer);
            var terms = ChildcareBodies.Terms(reader, Terms);
            if (Version is null) reader.Add("version", "request.required", "The version read with the arrangement is required.");
            return reader.Ok ? new EditChildcareCommand(id, Name ?? "", carer!, terms!, Version!.Value) : null;
        }
    }

    public sealed record ReviseChildcareBody(TermsBody? Terms);

    public sealed record UnpaidAbsencesBody(IReadOnlyList<string>? UnpaidAbsences);

    public sealed record GapsBody(IReadOnlyList<CareGapBody>? Gaps);

    public sealed record CloseChildcareBody(string? On);

    private static class ChildcareBodies
    {
        private static readonly NodaTime.Text.YearMonthPattern MonthPattern = NodaTime.Text.YearMonthPattern.CreateWithInvariantCulture("uuuu-MM");

        public static YearMonth? Month(FieldReader reader, string value)
        {
            var parsed = MonthPattern.Parse(value);
            if (parsed.Success) return parsed.Value;
            reader.Add("month", "request.invalid", "A month is yyyy-MM.");
            return null;
        }

        public static CarerInput? Carer(FieldReader reader, CarerBody? body)
            => body is null
                ? Missing(reader, "carer")
                : new CarerInput(body.Name ?? "", Blank(body.Pajemploi), Blank(body.Approval));

        public static TermsInput? Terms(FieldReader reader, TermsBody? body)
        {
            if (body is null) return Missing<TermsInput>(reader, "terms");
            var from = reader.Date(body.From, "terms.from");
            decimal? Number(string? value, string path, bool required)
            {
                if (value is null && required) reader.Add(path, "request.required", "This amount is required.");
                return reader.Decimal(value, path);
            }
            var net = Number(body.NetHourlyRate, "terms.netHourlyRate", true);
            var gross = Number(body.GrossHourlyRate, "terms.grossHourlyRate", false);
            var overtime = Number(body.OvertimePct, "terms.overtimePct", true) / 100m;
            var upkeep = Number(body.UpkeepPer9h, "terms.upkeepPer9h", true);
            var lunch = Number(body.LunchPrice, "terms.lunchPrice", false);
            var snack = Number(body.SnackPrice, "terms.snackPrice", false);
            MealProvider Provider(string? value, string path)
            {
                var parsed = value is null ? MealProvider.Nanny : MealProviderCodes.TryParse(value);
                if (parsed is null) reader.Add(path, "request.invalid", "A meal is provided by the nanny or the employer.");
                return parsed ?? MealProvider.Nanny;
            }
            var lunchProvider = Provider(body.LunchProvider, "terms.lunchProvider");
            var snackProvider = Provider(body.SnackProvider, "terms.snackProvider");
            var types = new List<WeekType>();
            if (body.WeekTypes is null) reader.Add("terms.weekTypes", "request.required", "The week types are required.");
            foreach (var (type, i) in (body.WeekTypes ?? []).Select((t, i) => (t, i)))
            {
                // Nullable annotations stop at collection elements: a null in the array binds as such.
                var path = $"terms.weekTypes[{i}]";
                if (type?.Name is null) reader.Add($"{path}.name", "request.required", "The name is required.");
                if (type?.Weeks is null) reader.Add($"{path}.weeks", "request.required", "The number of weeks is required.");
                var schedule = new List<WeekdaySlot>();
                foreach (var (slot, k) in (type?.Schedule ?? []).Select((s, k) => (s, k)))
                {
                    if (slot?.Day is not (>= 1 and <= 7) || slot is null)
                    {
                        reader.Add($"{path}.schedule[{k}].day", "request.invalid", "A day is 1 (Monday) to 7 (Sunday).");
                        continue;
                    }
                    var arrival = reader.Time(slot.Arrival, $"{path}.schedule[{k}].arrival");
                    var departure = reader.Time(slot.Departure, $"{path}.schedule[{k}].departure");
                    if (arrival is { } a && departure is { } d) schedule.Add(new WeekdaySlot((IsoDayOfWeek)slot.Day.Value, a, d, slot.Lunch ?? false, slot.Snack ?? false));
                }
                if (type?.Name is { } name && type.Weeks is { } weeks) types.Add(new WeekType(name, weeks, schedule));
            }
            var unpaid = Absences(reader, body.UnpaidAbsences, "terms.unpaidAbsences");
            return reader.Ok ? new TermsInput(from!.Value, net!.Value, gross, overtime!.Value, upkeep!.Value, lunch, types, snack, lunchProvider, snackProvider, unpaid) : null;
        }

        /// <summary>The absence reasons, each once; null when not sent.</summary>
        public static IReadOnlyList<AbsenceReason>? Absences(FieldReader reader, IReadOnlyList<string>? body, string path)
        {
            var reasons = body?.Select(AbsenceCodes.TryParse).ToList();
            if (reasons?.Any(u => u is null) == true)
                reader.Add(path, "request.invalid", "An absence is family, parent_rtt, child_sick, carer_absent, carer_training, holiday or paid_leave.");
            return reasons?.OfType<AbsenceReason>().Distinct().ToList();
        }

        public static IReadOnlyList<CareGap> Gaps(FieldReader reader, IReadOnlyList<CareGapBody>? body)
        {
            var gaps = new List<CareGap>();
            foreach (var (gap, i) in (body ?? []).Select((g, i) => (g, i)))
            {
                var monday = reader.Date(gap?.Monday, $"gaps[{i}].monday");
                if (gap?.Weeks is null) reader.Add($"gaps[{i}].weeks", "request.required", "The number of weeks is required.");
                else if (monday is { } m) gaps.Add(new CareGap(m, gap.Weeks.Value));
            }
            return gaps;
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static CarerInput? Missing(FieldReader reader, string path) => Missing<CarerInput>(reader, path);

        private static T? Missing<T>(FieldReader reader, string path) where T : class
        {
            reader.Add(path, "request.required", "This field is required.");
            return null;
        }
    }
}
