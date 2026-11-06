import { describe, expect, it } from 'vitest';
import { loanDetail } from '../testData';
import { contractValuesOf, isPlain, repaymentRank } from './contractValues';

const contract = loanDetail().contract;
const phase = contract.phases[0]!;

describe('isPlain', () => {
  it('is one fixed-rate annuity with nothing else', () => {
    expect(isPlain(contract)).toBe(true);
  });

  it.each([
    ['a deferral', { mode: 'deferred_partial' }], ['a variable rate', { rateType: 'variable' }], ['a count', { payments: 240 }],
    ['an imposed payment', { paymentOverride: '1250.00' }], ['an opening balance', { openingBalanceOverride: '150000.00' }],
    ['a note', { note: 'travaux' }],
  ])('is not with %s', (_, over) => {
    expect(isPlain({ ...contract, phases: [{ ...phase, ...over }] })).toBe(false);
  });

  it('is not with two phases', () => {
    expect(isPlain({ ...contract, phases: [phase, { ...phase, seq: 2 }] })).toBe(false);
  });
});

describe('contractValuesOf', () => {
  it('reads rates back in %, amounts as written, and empties what is absent', () => {
    const values = contractValuesOf({
      ...contract,
      phases: [{ ...phase, annualRate: '0.0412', payments: 24, paymentOverride: '1250.00' }],
      insurances: [{ mode: 'rate_on_initial', monthlyAmount: null, annualRate: '0.0036', startsOn: '2025-11-15', endsOn: null, note: null }],
    });
    expect(values).toEqual({
      principal: '200000,00', totalPayments: '240', disbursedOn: '2025-10-15', firstPaymentDate: '2025-11-15', rounding: 'half_up',
      firstPeriod: 'full',
      phases: [{ mode: 'annuity', rate: '4,12', rateType: 'fixed', payments: '24', paymentOverride: '1250,00', openingBalance: '', note: '' }],
      insurances: [{ mode: 'rate_on_initial', amount: '', rate: '0,36', startsOn: '2025-11-15', endsOn: '', note: '', follows: false }],
    });
  });
});

describe('repaymentRank', () => {
  it('reads the rank of a stored repayment the contract makes impossible', () => {
    expect(repaymentRank('repayments[2].amount')).toBe(2);
    expect(repaymentRank('repayments[0].onDate')).toBe(0);
    expect(repaymentRank('repayments[0].effect')).toBeNull();
    expect(repaymentRank('principal')).toBeNull();
  });
});
