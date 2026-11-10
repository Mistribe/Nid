using Nid.Documents.Contracts;
using static Nid.Host.Api.BankingEndpoints;
using static Nid.Host.Api.CatalogEndpoints;
using static Nid.Host.Api.ChildcareEndpoints;
using static Nid.Host.Api.DocumentEndpoints;
using static Nid.Host.Api.GoalEndpoints;
using static Nid.Host.Api.IncomeEndpoints;
using static Nid.Host.Api.InvestmentEndpoints;
using static Nid.Host.Api.PersonEndpoints;
using static Nid.Host.Api.RealEstateEndpoints;
using static Nid.Host.Api.SubscriptionEndpoints;
using static Nid.Host.Api.TaxEndpoints;

namespace Nid.Host.Mcp;

/// <summary>
/// One web write the assistant may propose: the REST route it replays when approved (or applied), so validation, access and
/// ownership rules stay the route's. <paramref name="Body"/> is the route's body type (null: none); <paramref name="Query"/>
/// the query parameters it reads. A route with an <c>{id}</c> is about that item: its proposal targets it, unless
/// <paramref name="OnItem"/> is false (a document's <c>{id}</c> is no item). <paramref name="Current"/> is the GET route, with the
/// same parameters, that reads what a correction or a deletion changes: its proposal's preview is <c>{ current }</c> (issue #321).
/// </summary>
public sealed record RouteAction(
    string Name, string Method, string Route, Type? Body, string Description, bool Destructive = false, string[]? Query = null, bool OnItem = true,
    string? Current = null)
{
    public string Tool => "propose_" + Name;

    /// <summary>`realestate.create_property`: the module is the route's first segment after `/api/v1/`.</summary>
    public string Kind => Route.Split('/')[3] + "." + Name;

    /// <summary>The route's parameters, in order: `{id:guid}` is `id`.</summary>
    public IReadOnlyList<string> RouteParameters { get; } = [.. Route.Split('/').Where(s => s.StartsWith('{')).Select(s => s.Trim('{', '}').Split(':')[0])];

    public bool Targeted => OnItem && RouteParameters.Contains("id");

    /// <summary>`{year:int}` is an integer; `{on}` an ISO date (a goal's progress entry); `{month}` a month; `{targetKind}` a word (a document link's); every other route parameter a UUID.</summary>
    public bool IsInteger(string parameter) => Route.Contains($"{{{parameter}:int}}", StringComparison.Ordinal);

    public bool IsDate(string parameter) => parameter == "on";

    public bool IsText(string parameter) => parameter == "targetKind";

    /// <summary>`{month}`: a statement's month, `yyyy-MM` (childcare spec decision 27).</summary>
    public bool IsMonth(string parameter) => parameter == "month";
}

/// <summary>
/// Spec 4D3: what the assistant can propose beyond the documents of 4D1, nearly every write of the web. Left out: the admin
/// area, tokens and sessions, preferences, the import, the FX and catalog syncs (admin or browser-only), the reads sent as
/// POST (simulations, the bank table's preview), the bank table (<c>propose_amortization</c>), and the tax profile and the
/// target allocation (issue #104): household settings replaced whole, which the assistant does not change; the purchase and sale scenarios (#122, #416),
/// hypothetical and read by no tool either; the stored documents' archive and unlink (the assistant reads, links and, since
/// #305, edits them through proposals; its uploads are `upload_document` and `request_document_upload`, no proposal: they change no figure); and the bank connections, sync and statement import, which need the person's browser.
/// </summary>
public static class RouteActions
{
    private const string Ids = "Ids come from the list_* and get_* tools. ";
    private const string Amounts = "Amounts are decimal strings with a point, dates ISO (yyyy-mm-dd). ";
    private const string Owners = "Owners' (a loan's borrowers') shares are basis points summing to 10000 (list_persons gives the person ids); visibility is 'shared' or 'private'. ";
    private const string TaxNotice = "Copy a French tax notice (avis d'imposition) as printed: year is the revenue year, issuedYear the notice's year; "
        + "one or two declarants (list_persons), rank 1 for D1, 2 for D2. Leave out any figure the notice does not print: never compute or guess one. "
        + "balance is positive when tax is due, negative when refunded. documentId (list_documents) is the notice's PDF: it is linked to each declarant, "
        + "so a joint notice's PDF becomes readable by both. ";
    private const string Versioned = "version is the one get_* returned for that line. ";
    private const string TaxNoticeRead = "/api/v1/tax/assessments/{id:guid}";
    private static readonly string Roles = string.Join(", ", DocumentRoles.All.Order(StringComparer.Ordinal));

    public static readonly IReadOnlyList<RouteAction> All =
    [
        // Real estate: properties.
        new("create_property", "POST", "/api/v1/realestate/properties", typeof(CreatePropertyBody),
            "Create a property (a home, a rental, land). " + Owners + Amounts),
        new("edit_property", "PATCH", "/api/v1/realestate/properties/{id:guid}", typeof(EditPropertyBody),
            "Edit a property's details; only the fields given change. " + Ids + Versioned + Amounts),
        new("add_property_valuation", "POST", "/api/v1/realestate/properties/{id:guid}/valuations", typeof(PropertyValuationBody),
            "Record a property's estimated value on a date. " + Ids + Amounts),
        new("delete_property_valuation", "DELETE", "/api/v1/realestate/properties/{id:guid}/valuations/{valuationId:guid}", null,
            "Remove a property's valuation. " + Ids, Destructive: true),
        new("add_property_flow", "POST", "/api/v1/realestate/properties/{id:guid}/flows", typeof(PropertyFlowBody),
            "Record a property's one-off income or expense (rent received, works, tax). " + Ids + Amounts),
        new("correct_property_flow", "PUT", "/api/v1/realestate/properties/{id:guid}/flows/{flowId:guid}", typeof(PropertyFlowBody),
            "Correct a property's income or expense. " + Ids + Amounts, Destructive: true),
        new("delete_property_flow", "DELETE", "/api/v1/realestate/properties/{id:guid}/flows/{flowId:guid}", null,
            "Remove a property's income or expense. " + Ids, Destructive: true),
        new("add_recurring_cost", "POST", "/api/v1/realestate/properties/{id:guid}/recurring-costs", typeof(PropertyRecurringCostBody),
            "Add a property's recurring cost (property tax, insurance, charges). " + Ids + Amounts),
        new("correct_recurring_cost", "PUT", "/api/v1/realestate/properties/{id:guid}/recurring-costs/{costId:guid}", typeof(PropertyRecurringCostBody),
            "Correct a property's recurring cost. " + Ids + Versioned + Amounts, Destructive: true),
        new("delete_recurring_cost", "DELETE", "/api/v1/realestate/properties/{id:guid}/recurring-costs/{costId:guid}", null,
            "Remove a property's recurring cost. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("add_tenancy", "POST", "/api/v1/realestate/properties/{id:guid}/tenancies", typeof(PropertyTenancyBody),
            "Add a tenancy (a lease and its rent) to a property. " + Ids + Amounts),
        new("correct_tenancy", "PUT", "/api/v1/realestate/properties/{id:guid}/tenancies/{tenancyId:guid}", typeof(PropertyTenancyBody),
            "Correct a tenancy. " + Ids + Versioned + Amounts, Destructive: true),
        new("delete_tenancy", "DELETE", "/api/v1/realestate/properties/{id:guid}/tenancies/{tenancyId:guid}", null,
            "Remove a tenancy. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("rent_revision", "POST", "/api/v1/realestate/properties/{id:guid}/tenancies/{tenancyId:guid}/revision", typeof(RentRevisionBody),
            "Accept a tenancy's yearly rent revision (get_rent_revisions lists those due): the rent changes from effectiveOn, between the "
            + "anniversary and today. With newRent, a free revision of a running tenancy: that rent from effectiveOn, after the tenancy's start "
            + "and up to today. " + Ids + Amounts),
        new("undo_rent_revision", "DELETE", "/api/v1/realestate/properties/{id:guid}/tenancies/{tenancyId:guid}/revision", null,
            "Undo a tenancy's last rent revision, while no rent is recorded from its start. " + Ids, Destructive: true),
        new("record_property_routine", "POST", "/api/v1/realestate/routine", typeof(PropertyRoutineBody),
            "The monthly property routine (get_property_routine): estimates and flows of several properties on one date. " + Ids + Amounts),
        new("undo_property_routine", "POST", "/api/v1/realestate/routine/undo", typeof(PropertyRoutineUndoBody),
            "Undo the facts a property routine recorded. " + Ids, Destructive: true),
        new("delete_property", "DELETE", "/api/v1/realestate/properties/{id:guid}", null,
            "Propose deleting a property created by mistake. The household confirms it by typing its name; a loan or a subscription insurance "
            + "linked to it refuses it. version is the one the deletion preview returned. " + Ids, Destructive: true, Query: ["version"],
            Current: "/api/v1/realestate/properties/{id:guid}/deletion"),

        // Real estate: loans.
        new("create_loan", "POST", "/api/v1/realestate/loans", typeof(CreateLoanBody),
            "Create a loan with its contract, linked to an existing property when propertyId is given (propose the property first, "
            + "then the loan once it is approved). Once approved, propose_amortization can attach the bank's table. " + Owners + Amounts),
        new("edit_loan", "PATCH", "/api/v1/realestate/loans/{id:guid}", typeof(EditLoanBody),
            "Edit a loan's details; only the fields given change. " + Ids),
        new("replace_loan_contract", "PUT", "/api/v1/realestate/loans/{id:guid}/contract", typeof(LoanContractBody),
            "Replace a loan's contract (amount, rate, phases, insurance). " + Ids + Versioned + Amounts, Destructive: true),
        new("add_loan_fee", "POST", "/api/v1/realestate/loans/{id:guid}/fees", typeof(LoanFeeBody),
            "Add a loan fee (application, guarantee, broker). " + Ids + Amounts),
        new("correct_loan_fee", "PUT", "/api/v1/realestate/loans/{id:guid}/fees/{feeId:guid}", typeof(LoanFeeBody),
            "Correct a loan fee. " + Ids + Versioned + Amounts, Destructive: true),
        new("delete_loan_fee", "DELETE", "/api/v1/realestate/loans/{id:guid}/fees/{feeId:guid}", null,
            "Remove a loan fee. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("add_loan_repayment", "POST", "/api/v1/realestate/loans/{id:guid}/repayments", typeof(LoanRepaymentBody),
            "Record an early repayment of a loan (simulate_early_repayment shows its effect first). " + Ids + Amounts),
        new("correct_loan_repayment", "PUT", "/api/v1/realestate/loans/{id:guid}/repayments/{eventId:guid}", typeof(LoanRepaymentBody),
            "Correct an early repayment. " + Ids + Versioned + Amounts, Destructive: true),
        new("delete_loan_repayment", "DELETE", "/api/v1/realestate/loans/{id:guid}/repayments/{eventId:guid}", null,
            "Remove an early repayment. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("delete_bank_schedule", "DELETE", "/api/v1/realestate/loans/{id:guid}/bank-schedule", null,
            "Remove the bank's amortisation table stored for a loan. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("delete_loan", "DELETE", "/api/v1/realestate/loans/{id:guid}", null,
            "Propose deleting a loan created by mistake. The household confirms it by typing its name. version is the one the deletion "
            + "preview returned. " + Ids, Destructive: true, Query: ["version"], Current: "/api/v1/realestate/loans/{id:guid}/deletion"),

        // Investments.
        new("create_envelope", "POST", "/api/v1/investments/envelopes", typeof(CreateEnvelopeBody),
            "Create an investment envelope (PEA, life insurance, savings account), with its opening and first valuation when known. A savings "
            + "account that is a regulated livret says which in 'regulated': 'livret_a', 'ldds', 'lep' or 'livret_jeune'. " + Owners + Amounts),
        new("edit_envelope", "PATCH", "/api/v1/investments/envelopes/{id:guid}", typeof(EditEnvelopeBody),
            "Edit an envelope's details; only the fields given change ('regulated' for a savings account's livret, 'clearRegulated' to remove it). " + Ids),
        new("add_valuation", "POST", "/api/v1/investments/envelopes/{id:guid}/valuations", typeof(ValuationBody),
            "Record an envelope's value on a date. " + Ids + Amounts),
        new("delete_valuation", "DELETE", "/api/v1/investments/envelopes/{id:guid}/valuations/{valuationId:guid}", null,
            "Remove an envelope's valuation. " + Ids, Destructive: true),
        new("add_flow", "POST", "/api/v1/investments/envelopes/{id:guid}/flows", typeof(FlowBody),
            "Record an envelope's movement: kind 'contribution', 'withdrawal', 'income' or 'expense'. " + Ids + Amounts),
        new("correct_flow", "PUT", "/api/v1/investments/envelopes/{id:guid}/flows/{flowId:guid}", typeof(FlowBody),
            "Correct an envelope's movement. " + Ids + Amounts, Destructive: true),
        new("delete_flow", "DELETE", "/api/v1/investments/envelopes/{id:guid}/flows/{flowId:guid}", null,
            "Remove an envelope's movement. " + Ids, Destructive: true),
        new("record_transfer", "POST", "/api/v1/investments/transfers", typeof(TransferBody),
            "Move money between two envelopes: a withdrawal from one and a contribution to the other. " + Ids + Amounts),
        new("record_routine", "POST", "/api/v1/investments/routine", typeof(RoutineBody),
            "The monthly investment routine (get_routine): values and contributions of several envelopes on one date. A contribution beyond a "
            + "livret's ceiling (ceilingRoom in get_routine) is refused. " + Ids + Amounts),
        new("undo_routine", "POST", "/api/v1/investments/routine/undo", typeof(RoutineUndoBody),
            "Undo the facts an investment routine recorded. " + Ids, Destructive: true),
        new("close_envelope", "POST", "/api/v1/investments/envelopes/{id:guid}/close", typeof(CloseBody),
            "Close an envelope on a date, with its final withdrawal when known. " + Ids + Amounts, Destructive: true),
        new("reopen_envelope", "POST", "/api/v1/investments/envelopes/{id:guid}/reopen", null,
            "Reopen a closed envelope. " + Ids),
        new("delete_envelope", "DELETE", "/api/v1/investments/envelopes/{id:guid}", null,
            "Propose deleting an envelope created by mistake (to close an account that ended, use close_envelope). The household confirms it "
            + "by typing its name. version is the one the deletion preview returned. " + Ids, Destructive: true, Query: ["version"],
            Current: "/api/v1/investments/envelopes/{id:guid}/deletion"),
        new("add_holding", "POST", "/api/v1/investments/envelopes/{id:guid}/holdings", typeof(HoldingBody),
            "Add a holding (support) to an envelope: a fund, ETF, share, the euro fund, a structured product or cash; kind 'euro_fund', 'fund', "
            + "'etf', 'equity', 'bond', 'structured', 'cash' or 'other'. Copy the name and ISIN as printed. " + Ids + Amounts),
        new("edit_holding", "PUT", "/api/v1/investments/envelopes/{id:guid}/holdings/{holdingId:guid}", typeof(HoldingBody),
            "Replace a holding's details (every field; set closedOn to close it). " + Ids + Versioned, Destructive: true),
        new("delete_holding", "DELETE", "/api/v1/investments/envelopes/{id:guid}/holdings/{holdingId:guid}", null,
            "Remove a holding that no snapshot uses. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("add_holdings_snapshot", "POST", "/api/v1/investments/envelopes/{id:guid}/holding-snapshots", typeof(SnapshotBody),
            "Record an envelope's composition on a statement's date: one line per holding (units, unit price, value as printed; no units for "
            + "the euro fund or cash). Replaces that date's snapshot. Copy the document as written and invent nothing. " + Ids + Amounts),
        new("replace_holdings_snapshot", "PUT", "/api/v1/investments/envelopes/{id:guid}/holding-snapshots/{snapshotId:guid}", typeof(SnapshotBody),
            "Replace a recorded snapshot of an envelope's holdings. " + Ids + Amounts, Destructive: true),
        new("delete_holdings_snapshot", "DELETE", "/api/v1/investments/envelopes/{id:guid}/holding-snapshots/{snapshotId:guid}", null,
            "Remove a snapshot of an envelope's holdings. " + Ids, Destructive: true),
        new("add_holding_movement", "POST", "/api/v1/investments/envelopes/{id:guid}/holdings/{holdingId:guid}/movements", typeof(MovementBody),
            "Record a movement on one holding of an envelope: kind 'allocation_in' or 'allocation_out' (its share of a payment or withdrawal), "
            + "'maturity', 'fee' (management fee taken in units), 'interest' (euro fund profit sharing) or 'income'. The amount is above zero; "
            + "units are signed (bought +, deducted −). A switch between holdings is record_holding_switch. Copy the document as written and invent nothing. "
            + Ids + Amounts),
        new("record_holding_switch", "POST", "/api/v1/investments/envelopes/{id:guid}/switches", typeof(SwitchBody),
            "Record a switch (arbitrage) between holdings of an envelope, every leg at once: 'from' lists the holdings the money leaves and 'to' "
            + "the holdings it goes to (one or more each, e.g. one fund into five), each leg with its amount, units and unit price as printed on "
            + "the notice; maturity true when the sources are structured products reaching their term or called. A switch fee is a separate "
            + "'fee' movement. Copy the document as written and invent nothing. "
            + Ids + Amounts),
        new("save_holding_cost_report", "PUT", "/api/v1/investments/envelopes/{id:guid}/holdings/{holdingId:guid}/cost-reports/{year:int}", typeof(CostReportBody),
            "Record one holding's yearly costs as stated on the insurer's annual document (gross return, total fees, net return, in percent; "
            + "optional contract fees, support fees, retrocession and 5-year annualized net return). Replaces that year's report. Copy the document "
            + "as written and invent nothing. " + Ids, Destructive: true),

        // Income.
        new("create_income_source", "POST", "/api/v1/income/sources", typeof(CreateIncomeSourceBody),
            "Create an income source (a salary, a pension, an allowance) with its first period. Once approved, propose_payslip and "
            + "the other income tools work on it. " + Owners + Amounts),
        new("edit_income_source", "PATCH", "/api/v1/income/sources/{id:guid}", typeof(EditIncomeSourceBody),
            "Edit an income source's details; only the fields given change. " + Ids),
        new("add_income_phase", "POST", "/api/v1/income/sources/{id:guid}/phases", typeof(IncomePhaseBody),
            "Add a period to an income source: its expected pay from a date. A payslip earlier than the source's first period is "
            + "added this way first. " + Ids + Amounts),
        new("correct_income_phase", "PUT", "/api/v1/income/sources/{id:guid}/phases/{phaseId:guid}", typeof(IncomePhaseCorrectionBody),
            "Correct an income period. " + Ids + Versioned + Amounts, Destructive: true),
        new("delete_income_phase", "DELETE", "/api/v1/income/sources/{id:guid}/phases/{phaseId:guid}", null,
            "Remove an income period. " + Ids + Versioned, Destructive: true, Query: ["version"]),
        new("change_income_phase", "POST", "/api/v1/income/sources/{id:guid}/phase-changes", typeof(IncomePhaseChangeBody),
            "Change an income source's expected pay from a date (a raise, a new contract): the open period ends the day before. "
            + Ids + Amounts),
        new("add_one_off_income", "POST", "/api/v1/income/sources/{id:guid}/one-offs", typeof(IncomeOneOffBody),
            "Record a one-off income of a source (a bonus, a final settlement, an adjustment). " + Ids + Amounts),
        new("correct_one_off_income", "PUT", "/api/v1/income/sources/{id:guid}/one-offs/{flowId:guid}", typeof(IncomeOneOffBody),
            "Correct a one-off income. " + Ids + Amounts, Destructive: true),
        new("delete_one_off_income", "DELETE", "/api/v1/income/sources/{id:guid}/one-offs/{flowId:guid}", null,
            "Remove a one-off income. " + Ids, Destructive: true),
        new("close_income_source", "POST", "/api/v1/income/sources/{id:guid}/close", typeof(CloseIncomeSourceBody),
            "End an income source on a date. " + Ids + Amounts, Destructive: true),
        new("reopen_income_source", "POST", "/api/v1/income/sources/{id:guid}/reopen", null,
            "Reopen an ended income source. " + Ids),
        new("delete_income_source", "DELETE", "/api/v1/income/sources/{id:guid}", null,
            "Propose deleting an income source created by mistake (to end one that stopped, use close_income_source). The household confirms it "
            + "by typing its name. version is the one the deletion preview returned. " + Ids, Destructive: true, Query: ["version"],
            Current: "/api/v1/income/sources/{id:guid}/deletion"),

        // Childcare (childminder, Pajemploi).
        new("create_childcare", "POST", "/api/v1/childcare/", typeof(CreateChildcareBody),
            "Record a childminder's contract (assistante maternelle, année incomplète): childId is a dependent (list_persons); carer name, "
            + "Pajemploi and approval numbers; terms from a date with the net hourly rate, overtimePct (10 = 10 %), the upkeep for 9 hours, unpaidAbsences (the absence reasons deducted from the salary, among family, parent_rtt, child_sick, carer_absent, holiday and paid_leave; absent to keep the current ones), "
            + "lunchPrice, snackPrice, lunchProvider and snackProvider (nanny or employer, only the nanny's meals are paid), weekTypes (each a name, its weeks and its weekly schedule: day 1 = Monday, arrival and departure HH:mm, lunch, snack; together 1 to 46 weeks); "
            + "gaps are weeks without care, each from a Monday. " + Owners + Amounts),
        new("edit_childcare", "PUT", "/api/v1/childcare/{id:guid}", typeof(EditChildcareBody),
            "Correct an arrangement's name, carer and current terms in place (terms.from stays the current one's; a change from a date is propose_revise_childcare). "
            + Ids + Versioned + Amounts),
        new("revise_childcare", "POST", "/api/v1/childcare/{id:guid}/revisions", typeof(ReviseChildcareBody),
            "Revise an arrangement from a date after its current terms' start: the current terms end the day before. " + Ids + Amounts),
        new("replace_childcare_gaps", "PUT", "/api/v1/childcare/{id:guid}/gaps", typeof(GapsBody),
            "Replace an arrangement's weeks without care (each from a Monday, 1 to 52 weeks; send them all). " + Ids + Amounts),
        new("set_childcare_week_override", "PUT", "/api/v1/childcare/{id:guid}/week-overrides/{monday}", typeof(WeekOverrideBody),
            "Make one week (monday yyyy-MM-dd, a Monday of the contract) follow another week type: weekType is the rank in get_childcare's "
            + "weekTypes, 0 for the first; the rank the rotation gives removes the exception. " + Ids),
        new("close_childcare", "POST", "/api/v1/childcare/{id:guid}/close", typeof(CloseChildcareBody),
            "End an arrangement on its last day (on). " + Ids + Amounts),
        new("reopen_childcare", "POST", "/api/v1/childcare/{id:guid}/reopen", null,
            "Reopen an ended arrangement. " + Ids),
        new("save_childcare_statement", "PUT", "/api/v1/childcare/{id:guid}/statements/{month}", typeof(StatementBody),
            "Propose a month's statement as a draft (month yyyy-MM): every day of the month as get_childcare_statement gives it, the deviations "
            + "changed (present with arrival and departure HH:mm, lunch, snack and, for a planned day cut short, an optional reason; or absent with a reason: family, parent_rtt, child_sick, carer_absent, "
            + "holiday, paid_leave), the mileage, the CMG and the contributions left to the household (contributions, absent when all are covered) of the Pajemploi summary. For a past month, summary (careDays, hours, absence for the planned days "
            + "left over) replaces the days, and declared gives the declaration fields entered by hand (normalHours, activityDays, paidLeaveDays, complementaryHours, overtimeHours, netPay, upkeep, meals). Validating it stays the person's gesture in the web app. "
            + Ids + Versioned + Amounts),

        // Savings goals.
        new("create_goal", "POST", "/api/v1/goals/", typeof(CreateGoalBody),
            "Create a savings goal: kind 'savings' with a targetAmount, or 'emergency_fund' with months (1 to 24, 6 by default) and "
            + "no amount; an optional targetDate; funded by envelopes (envelopeId, shareBp of the envelope, 10000 = all of it, "
            + "list_envelopes gives the ids) or, without funding, by progress entries. " + Owners + Amounts),
        new("edit_goal", "PUT", "/api/v1/goals/{id:guid}", typeof(EditGoalBody),
            "Replace a goal's name, target, months, target date, note and funding (send them all; the kind stays). " + Ids + Versioned + Amounts),
        new("close_goal", "POST", "/api/v1/goals/{id:guid}/close", null,
            "Close a goal: it stays readable and no longer holds its envelopes' shares. " + Ids),
        new("reopen_goal", "POST", "/api/v1/goals/{id:guid}/reopen", null,
            "Reopen a closed goal. " + Ids),
        new("record_goal_progress", "PUT", "/api/v1/goals/{id:guid}/progress/{on}", typeof(GoalProgressBody),
            "Record the amount a goal without envelopes has reached on a date (on, not in the future); it replaces that date's entry. " + Ids + Amounts),
        new("delete_goal_progress", "DELETE", "/api/v1/goals/{id:guid}/progress/{on}", null,
            "Remove a goal's progress entry of a date. " + Ids, Destructive: true),
        new("delete_goal", "DELETE", "/api/v1/goals/{id:guid}", null,
            "Propose deleting a goal created by mistake (to close a goal that ended, use close_goal). The household confirms it "
            + "by typing its name. version is the one the deletion preview returned. " + Ids, Destructive: true, Query: ["version"],
            Current: "/api/v1/goals/{id:guid}/deletion"),

        // Subscriptions.
        new("create_subscription", "POST", "/api/v1/subscriptions/", typeof(CreateSubscriptionBody),
            "Create a subscription with its first billing period. " + Owners + Amounts),
        new("edit_subscription", "PATCH", "/api/v1/subscriptions/{id:guid}", typeof(EditSubscriptionBody),
            "Edit a subscription's details; only the fields given change. Contract terms (all ISO dates yyyy-mm-dd): "
            + "anniversaryOn is the contract's yearly renewal date; notice {count, unit: day, week, month or year} is the notice period "
            + "counted back from it (a notice needs an anniversaryOn or a commitmentEndsOn); commitmentEndsOn is the end of the "
            + "minimum commitment; cancellableFrom is the date from which it can be cancelled at any time. Each has a clear flag "
            + "(clearAnniversaryOn, clearNotice, clearCommitmentEndsOn, clearCancellableFrom) to remove it, never sent with its value; "
            + "terms not mentioned are kept. get_subscription returns them as contractTerms, with the computed cancellation. " + Ids),
        new("set_insurance", "PUT", "/api/v1/subscriptions/{id:guid}/insurance", typeof(InsuranceBody),
            "Record what an insurance subscription covers: kind, insured property, guarantees; it replaces the whole record. "
            + "Read the current record with get_subscription first and resend everything you keep: what is not sent is removed. "
            + "keepProperty=true (without propertyId, else refused) leaves the stored property link as it is, for a writer who cannot read it; "
            + "unless you are setting the link or deliberately removing it, always send keepProperty: true, because omitting it removes any "
            + "link, seen or not. " + Ids),
        new("add_subscription_phase", "POST", "/api/v1/subscriptions/{id:guid}/phases", typeof(PhaseBody),
            "Add a billing period to a subscription. " + Ids + Amounts),
        new("end_subscription_phase", "PATCH", "/api/v1/subscriptions/{id:guid}/phases/{phaseId:guid}", typeof(ClosePhaseBody),
            "End a subscription's billing period on a date (a cancellation). " + Ids + Amounts, Destructive: true),
        new("change_subscription_price", "POST", "/api/v1/subscriptions/{id:guid}/price-changes", typeof(PriceChangeBody),
            "Change a subscription's price from a date (propose_invoice does it from an invoice). " + Ids + Amounts),
        new("add_purchase", "POST", "/api/v1/subscriptions/{id:guid}/purchases", typeof(PurchaseInput),
            "Record a one-off purchase linked to a subscription. " + Ids + Amounts),
        new("delete_purchase", "DELETE", "/api/v1/subscriptions/{id:guid}/purchases/{purchaseId:guid}", null,
            "Remove a purchase. " + Ids, Destructive: true),
        new("create_provider", "POST", "/api/v1/subscriptions/providers", typeof(CreateProviderBody),
            "Add a provider to the subscriptions catalog when the household's is not listed."),

        // Documents (issue #179): the web's link, so a document already in Nid follows its element, history included.
        new("link_document", "POST", "/api/v1/documents/{id:guid}/links", typeof(LinkDocumentBody),
            "Link a document already in Nid to what it is about: id is the document's id (list_documents, inbox=true for those not filed "
            + "yet); targetKind 'core.item' (an envelope, property, loan, income source, subscription…), 'income.phase' (an income "
            + "period, get_income_source), 'income.one_off' (a one-off income), 'core.person' or 'core.household' (no targetId); role: "
            + DocumentTools.RoleList + ". Nothing else changes: a payslip linked to its period records no pay. " + Ids, OnItem: false),
        // #305: filing: one document or link replays the web's route; a round is one proposal, approved at once.
        new("edit_document", "PUT", "/api/v1/documents/{id:guid}", typeof(EditDocumentBody),
            "Replace a document's title, date (documentDate) and note: send all three, read_document gives them; what is left out is removed. id is the "
            + "document's id (list_documents). Several documents, or only a date: propose_file_documents. " + Ids, Destructive: true, OnItem: false),
        new("set_document_role", "PUT", "/api/v1/documents/{id:guid}/links/{targetKind}/{targetId:guid}", typeof(SetLinkRoleBody),
            "Change the role of a document's link: id, targetKind and targetId are the document's and its link's (list_documents; the household's targetId "
            + "is all zeros). Role: " + Roles + ". " + Ids, OnItem: false),
        new("file_documents", "POST", "/api/v1/documents/filing", typeof(FileDocumentsBody),
            "A filing round, up to 200 lines, one proposal approved in one action, applied whole or not at all. edits: { id, and any of title, "
            + "documentDate, note }, only what is given changes. roles: { id, targetKind, targetId, role }, the link as list_documents gives it (no "
            + "targetId for the household); role: " + Roles + ". A refusal names its line, e.g. payload.body.edits[3].documentDate. " + Ids, OnItem: false),

        // Tax notices (issue #182): the route links the source PDF, so an approved proposal does too.
        new("add_tax_year", "POST", "/api/v1/tax/assessments", typeof(AddTaxAssessmentBody), TaxNotice + Ids + Amounts),
        new("correct_tax_year", "PUT", "/api/v1/tax/assessments/{id:guid}", typeof(CorrectTaxAssessmentBody),
            TaxNotice + "Replaces the whole notice: send every field again with the version list_tax_years gave. " + Ids + Amounts,
            Destructive: true, OnItem: false, Current: TaxNoticeRead),
        new("delete_tax_year", "DELETE", "/api/v1/tax/assessments/{id:guid}", null, "Delete a tax notice entered by mistake. " + Ids,
            Destructive: true, OnItem: false, Current: TaxNoticeRead),

        // Bank sync (§8): a bank account's settings; connecting a bank stays in the browser.
        new("bank_account_update", "PATCH", "/api/v1/banking/accounts/{id:guid}", typeof(BankAccountBody),
            "Change a bank account's settings; only the fields given change: envelopeId associates it with a checking, savings or other "
            + "envelope (clearEnvelope: true dissociates it), cardOfAccountId attaches a card to its account, autoApplyBalance writes its "
            + "month-end balances as valuations instead of proposing them, ignored stops following it. id is the bank account's id (Réglages › Banques in the web app); envelope ids come from list_envelopes.",
            OnItem: false),
        // 5C: a line's link and its rules; linking itself is propose_bank_link.
        new("unlink_bank_transaction", "POST", "/api/v1/banking/transactions/{id:guid}/unlink", null,
            "Take a bank line's link (or its ignored mark) off, so it is unlinked again; its rule stays. id is the line's id, from list_bank_transactions.",
            Destructive: true, OnItem: false),
        new("ignore_bank_transaction", "POST", "/api/v1/banking/transactions/{id:guid}/ignore", null,
            "Mark an unlinked bank line as ignored: no charge is expected for it. id is the line's id, from list_bank_transactions.", OnItem: false),
        new("delete_bank_rule", "DELETE", "/api/v1/banking/rules/{id:guid}", null,
            "Delete one of your bank rules (a label linked to an item on an account); the lines it linked stay linked. id is the rule's id.",
            Destructive: true, OnItem: false),

        // Household.
        new("create_dependent", "POST", "/api/v1/core/persons/dependents", typeof(CreateDependentBody),
            "Add a dependent (a child) to the household, so items can be owned in their name."),
        new("set_retirement", "PUT", "/api/v1/core/persons/{id:guid}/retirement", typeof(RetirementBody),
            "Set a person's birth date and retirement age (50 to 75), which get_projection's 'retire:<personId>' needs. id is the person's id, from "
            + "list_persons. The route replaces both: resend the one that does not change, as list_persons gives it; without a birthDate, the date is cleared. "
            + Amounts, Destructive: true, OnItem: false),
    ];
}
