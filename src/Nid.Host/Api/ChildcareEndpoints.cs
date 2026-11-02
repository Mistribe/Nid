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

        api.MapPut("/{id:guid}/gaps", async (Guid id, GapsBody body, IInvoker invoker, CancellationToken ct) =>
            {
                var reader = new FieldReader();
                var gaps = ChildcareBodies.Gaps(reader, body.Gaps);
                return !reader.Ok
                    ? reader.Problem()
                    : (await invoker.InvokeAsync(new ReplaceGapsCommand(new ItemId(id), gaps), ct)).ToHttp(x => new IdResponse(x.Value));
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

        api.MapGet("/{id:guid}/statements", async (Guid id, IInvoker invoker, CancellationToken ct)
            => (await invoker.InvokeAsync(new ListStatementsQuery(new ItemId(id)), ct)).ToHttp(list => list))
            .Returns<ChildcareStatementList>();

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
    public sealed record StatementDayBody(string? Date, bool? Present, string? Arrival, string? Departure, bool? Meal, string? Absence);

    /// <summary>Every day of the month, the month's mileage and CMG as decimal strings, and the <c>version</c> read (0 when nothing was stored).</summary>
    public sealed record StatementBody(IReadOnlyList<StatementDayBody>? Days, string? Mileage, string? Cmg, uint? Version)
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
                    reader.Add($"days[{i}].absence", "request.invalid", "An absence is family, parent_rtt, child_sick, carer_absent or holiday.");
                if (date is { } d) days.Add(new DayInput(d, present, arrival, departure, day?.Meal ?? false, absence));
            }
            var mileage = reader.Decimal(Mileage, "mileage") ?? 0m;
            var cmg = reader.Decimal(Cmg, "cmg");
            if (Version is null) reader.Add("version", "request.required", "The version read with the statement is required.");
            return reader.Ok ? new SaveStatementCommand(id, month!.Value, days, mileage, cmg, Version!.Value) : null;
        }
    }

    public sealed record VersionBody(uint? Version);

    public sealed record CarerBody(string? Name, string? Pajemploi, string? Approval);

    /// <summary>ISO day (1 = Monday), times <c>HH:mm</c>.</summary>
    public sealed record WeekdaySlotBody(int? Day, string? Arrival, string? Departure, bool? Meal);

    /// <summary>Rates as decimal strings; <c>overtimePct</c> in percent (10 = 10 %).</summary>
    public sealed record TermsBody(
        string? From, string? NetHourlyRate, string? GrossHourlyRate, string? OvertimePct, string? UpkeepPer9h, string? MealPrice, int? ProgrammedWeeks,
        IReadOnlyList<WeekdaySlotBody>? Schedule);

    public sealed record CareGapBody(string? Monday, int? Weeks);

    public sealed record CreateChildcareBody(
        string? Name, Guid? ChildId, CarerBody? Carer, TermsBody? Terms, IReadOnlyList<CareGapBody>? Gaps, string? Visibility, IReadOnlyList<OwnerBody>? Owners)
    {
        internal CreateChildcareCommand? ToCommand(FieldReader reader)
        {
            var visibility = reader.Visibility(Visibility, "visibility");
            if (ChildId is null) reader.Add("childId", "request.required", "The child is required.");
            var carer = ChildcareBodies.Carer(reader, Carer);
            var terms = ChildcareBodies.Terms(reader, Terms);
            var gaps = ChildcareBodies.Gaps(reader, Gaps);
            return reader.Ok
                ? new CreateChildcareCommand(Name ?? "", new PersonId(ChildId!.Value), carer!, terms!, gaps, visibility!.Value,
                    (Owners ?? []).Select(o => new OwnerShare(new PersonId(o.PersonId), o.ShareBp)).ToList())
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
            var meal = Number(body.MealPrice, "terms.mealPrice", false);
            if (body.ProgrammedWeeks is null) reader.Add("terms.programmedWeeks", "request.required", "The programmed weeks are required.");
            var schedule = new List<WeekdaySlot>();
            foreach (var (slot, i) in (body.Schedule ?? []).Select((s, i) => (s, i)))
            {
                // Nullable annotations stop at collection elements: a null in the array binds as such.
                if (slot?.Day is not (>= 1 and <= 7) || slot is null)
                {
                    reader.Add($"terms.schedule[{i}].day", "request.invalid", "A day is 1 (Monday) to 7 (Sunday).");
                    continue;
                }
                var arrival = reader.Time(slot.Arrival, $"terms.schedule[{i}].arrival");
                var departure = reader.Time(slot.Departure, $"terms.schedule[{i}].departure");
                if (arrival is { } a && departure is { } d) schedule.Add(new WeekdaySlot((IsoDayOfWeek)slot.Day.Value, a, d, slot.Meal ?? false));
            }
            return reader.Ok ? new TermsInput(from!.Value, net!.Value, gross, overtime!.Value, upkeep!.Value, meal, body.ProgrammedWeeks!.Value, schedule) : null;
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
