using static Nid.Host.Api.CatalogEndpoints;
using static Nid.Host.Api.IncomeEndpoints;
using static Nid.Host.Api.InvestmentEndpoints;
using static Nid.Host.Api.PersonEndpoints;
using static Nid.Host.Api.RealEstateEndpoints;
using static Nid.Host.Api.SubscriptionEndpoints;

namespace Nid.Host.Mcp;

/// <summary>
/// One web write the assistant may propose: the REST route it replays when approved (or applied), so validation, access and
/// ownership rules stay the route's. <paramref name="Body"/> is the route's body type (null: none); <paramref name="Query"/>
/// the query parameters it reads. A route with an <c>{id}</c> is about that item: its proposal targets it.
/// </summary>
public sealed record RouteAction(
    string Name, string Method, string Route, Type? Body, string Description, bool Destructive = false, string[]? Query = null)
{
    public string Tool => "propose_" + Name;

    /// <summary>`realestate.create_property`: the module is the route's first segment after `/api/v1/`.</summary>
    public string Kind => Route.Split('/')[3] + "." + Name;

    /// <summary>The route's parameters, in order: `{id:guid}` is `id`.</summary>
    public IReadOnlyList<string> RouteParameters { get; } = [.. Route.Split('/').Where(s => s.StartsWith('{')).Select(s => s.Trim('{', '}').Split(':')[0])];

    public bool Targeted => RouteParameters.Contains("id");
}

/// <summary>
/// Spec 4D3: what the assistant can propose beyond the documents of 4D1, nearly every write of the web. Left out: the admin
/// area, tokens and sessions, preferences, the import, the FX and catalog syncs (admin or browser-only), the reads sent as
/// POST (simulations, the bank table's preview), and the bank table (<c>propose_amortization</c>).
/// </summary>
public static class RouteActions
{
    private const string Ids = "Ids come from the list_* and get_* tools. ";
    private const string Amounts = "Amounts are decimal strings with a point, dates ISO (yyyy-mm-dd). ";
    private const string Owners = "Owners' (a loan's borrowers') shares are basis points summing to 10000 (list_persons gives the person ids); visibility is 'shared' or 'private'. ";
    private const string Versioned = "version is the one get_* returned for that line. ";

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
        new("record_property_routine", "POST", "/api/v1/realestate/routine", typeof(PropertyRoutineBody),
            "The monthly property routine (get_property_routine): estimates and flows of several properties on one date. " + Ids + Amounts),
        new("undo_property_routine", "POST", "/api/v1/realestate/routine/undo", typeof(PropertyRoutineUndoBody),
            "Undo the facts a property routine recorded. " + Ids, Destructive: true),

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

        // Investments.
        new("create_envelope", "POST", "/api/v1/investments/envelopes", typeof(CreateEnvelopeBody),
            "Create an investment envelope (PEA, life insurance, savings account), with its opening and first valuation when known. "
            + Owners + Amounts),
        new("edit_envelope", "PATCH", "/api/v1/investments/envelopes/{id:guid}", typeof(EditEnvelopeBody),
            "Edit an envelope's details; only the fields given change. " + Ids),
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
            "The monthly investment routine (get_routine): values and contributions of several envelopes on one date. " + Ids + Amounts),
        new("undo_routine", "POST", "/api/v1/investments/routine/undo", typeof(RoutineUndoBody),
            "Undo the facts an investment routine recorded. " + Ids, Destructive: true),
        new("close_envelope", "POST", "/api/v1/investments/envelopes/{id:guid}/close", typeof(CloseBody),
            "Close an envelope on a date, with its final withdrawal when known. " + Ids + Amounts, Destructive: true),
        new("reopen_envelope", "POST", "/api/v1/investments/envelopes/{id:guid}/reopen", null,
            "Reopen a closed envelope. " + Ids),

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

        // Subscriptions.
        new("create_subscription", "POST", "/api/v1/subscriptions/", typeof(CreateSubscriptionBody),
            "Create a subscription with its first billing period. " + Owners + Amounts),
        new("edit_subscription", "PATCH", "/api/v1/subscriptions/{id:guid}", typeof(EditSubscriptionBody),
            "Edit a subscription's details; only the fields given change. " + Ids),
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

        // Household.
        new("create_dependent", "POST", "/api/v1/core/persons/dependents", typeof(CreateDependentBody),
            "Add a dependent (a child) to the household, so items can be owned in their name."),
    ];
}
