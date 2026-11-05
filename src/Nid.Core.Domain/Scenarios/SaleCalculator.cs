using Nid.Core.Contracts;

namespace Nid.Core.Domain.Scenarios;

/// <summary>
/// Issue #416: what a sale leaves the household, in the reference currency. <c>NetSeller</c> = price − agency fees − tax − the loans'
/// balance at the sale − penalties; <c>MonthlyChange</c> = the loans' payments and the charges less the rent that stop with it.
/// </summary>
public sealed record SaleResult(
    decimal Tax, decimal LoansRepaid, decimal NetSeller, decimal LoanPayments, decimal PropertyNet, decimal MonthlyChange);

public static partial class ScenarioCalculator
{
    /// <summary>What the projection's sale takes off the price besides the loans: the agency fees, the tax and the penalties.</summary>
    public static decimal SaleCosts(SaleScenario doc, decimal tax) => doc.AgencyFees + tax + doc.Penalties;

    /// <summary><paramref name="outcome"/> is RealEstate's reading of the sale: its loans' balance and payments, its charges less rent.</summary>
    public static SaleResult Sale(SaleScenario doc, decimal tax, SaleOutcome outcome) => new(
        Cent(tax), Cent(outcome.Settled), Cent(doc.Price - SaleCosts(doc, tax) - outcome.Settled), Cent(outcome.LoanPayments),
        Cent(outcome.MonthlyNet), Cent(outcome.LoanPayments + outcome.MonthlyNet));
}
