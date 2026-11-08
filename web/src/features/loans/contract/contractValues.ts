import type { LoanDetail } from '../api';
import type { ContractValues, InsuranceLine, PhaseLine } from '../new/loanForm';
import { fractionToPercent } from '../rate';
import { toFieldAmount } from '@/lib/amount';

type Contract = LoanDetail['contract'];

/** 3B2 design §5: one fixed-rate annuity with no count, imposed payment, opening balance or note opens as a plain loan. */
export function isPlain(c: Contract): boolean {
  const [only, ...rest] = c.phases;
  return !!only && rest.length === 0 && only.mode === 'annuity' && only.rateType === 'fixed' && only.payments == null
    && !only.paymentOverride && !only.openingBalanceOverride && !only.note;
}

/** The stored contract as the form's values: rates back to % by moving the point, amounts as the server wrote them. */
export function contractValuesOf(c: Contract): ContractValues {
  // The server writes only its own enumerations: the casts name them for the form's types.
  return {
    principal: toFieldAmount(c.principal), totalPayments: String(c.totalPayments), disbursedOn: c.disbursedOn, firstPaymentDate: c.firstPaymentDate,
    rounding: c.rounding as ContractValues['rounding'], firstPeriod: c.firstPeriod as ContractValues['firstPeriod'],
    phases: c.phases.map((p) => ({
      mode: p.mode as PhaseLine['mode'], rate: fractionToPercent(p.annualRate), rateType: p.rateType as PhaseLine['rateType'],
      payments: p.payments == null ? '' : String(p.payments), paymentOverride: toFieldAmount(p.paymentOverride ?? ''),
      openingBalance: toFieldAmount(p.openingBalanceOverride ?? ''), note: p.note ?? '',
    })),
    // A stored insurance keeps its start (3B2 design §4.3).
    insurances: c.insurances.map((i) => ({
      mode: i.mode as InsuranceLine['mode'], amount: toFieldAmount(i.monthlyAmount ?? ''), rate: i.annualRate ? fractionToPercent(i.annualRate) : '',
      startsOn: i.startsOn, endsOn: i.endsOn ?? '', note: i.note ?? '', follows: false,
    })),
  };
}

/** The rank, in the detail's `repayments`, of a stored repayment a refusal names (`repayments[i].amount` or `.onDate`). */
export function repaymentRank(path: string): number | null {
  const match = /^repayments\[(\d+)\]\.(?:amount|onDate)$/.exec(path);
  return match ? Number(match[1]) : null;
}
