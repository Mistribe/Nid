import type { TFunction } from 'i18next';
import { describe, expect, it } from 'vitest';
import { contractPath, contractSchema, newInsurance, newLoanPath, newLoanSchema, newPhase, plusOneMonth, toContractBody, toCreateBody, type NewLoanValues } from './newLoan';

const t = ((key: string) => key) as unknown as TFunction;

const valid = (over: Partial<NewLoanValues> = {}): NewLoanValues => ({
  name: 'Prêt maison', lender: 'Banque', currency: 'EUR', note: '', visibility: 'private',
  owners: [{ personId: 'alice', share: '100' }],
  principal: '200000', totalPayments: '240', disbursedOn: '2026-09-12', firstPaymentDate: '2026-10-12',
  rounding: 'half_up', firstPeriod: 'full', phases: [{ ...newPhase(), rate: '3,5' }], insurances: [], ...over,
});

const issues = (v: NewLoanValues) => {
  const r = newLoanSchema(t).safeParse(v);
  return r.success ? [] : r.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`);
};

describe('plusOneMonth', () => {
  it.each([
    ['2026-09-12', '2026-10-12'], ['2026-12-15', '2027-01-15'], ['2026-01-31', '2026-02-28'], ['2028-01-31', '2028-02-29'],
    ['2026-03-31', '2026-04-30'],
  ])('puts the month after %s on %s, its day clamped to that month', (from, to) => {
    expect(plusOneMonth(from)).toBe(to);
  });
});

describe('the schema', () => {
  it('accepts a plain loan, its rate typed with a comma or a dot', () => {
    expect(issues(valid())).toEqual([]);
    expect(issues(valid({ phases: [{ ...newPhase(), rate: '3.5' }] }))).toEqual([]);
    expect(issues(valid({ principal: '999999999999,99' }))).toEqual([]);
  });

  it.each(['100', '3,12345', '-1', 'abc', ''])('refuses the rate %j', (rate) => {
    expect(issues(valid({ phases: [{ ...newPhase(), rate }] }))).toEqual(['phases.0.rate: loan_form.rate_format']);
  });

  it.each(['200000,005', '1000000000000', '0', '0,00', '-5', ''])('refuses the principal %j', (principal) => {
    expect(issues(valid({ principal }))).toEqual(['principal: loan_form.amount_format']);
  });

  it.each(['0', '601', '12,5', ''])('refuses a duration of %j months', (totalPayments) => {
    expect(issues(valid({ totalPayments }))).toEqual(['totalPayments: loan_form.duration_range']);
  });

  it('refuses a first payment before the disbursement, not on its day', () => {
    expect(issues(valid({ firstPaymentDate: '2026-09-11' }))).toEqual(['firstPaymentDate: loan_form.first_after_disbursed']);
    expect(issues(valid({ firstPaymentDate: '2026-09-12' }))).toEqual([]);
  });

  it('asks every phase but the last for its count, and refuses more payments than the loan has', () => {
    const line = (payments: string) => ({ ...newPhase(), rate: '3', payments });
    expect(issues(valid({ phases: [line(''), line('')] }))).toEqual(['phases.0.payments: loan_form.payments_required']);
    expect(issues(valid({ phases: [line('200'), line('41')] }))).toEqual(['totalPayments: loan_form.phases_exceed_duration']);
    expect(issues(valid({ phases: [line('200'), line('40')] }))).toEqual([]);
    expect(issues(valid({ phases: [line('200'), line('')] }))).toEqual([]);
  });

  it('reports the unanswered visibility together with the cross-field checks', () => {
    const v = { ...valid({ firstPaymentDate: '2026-09-11' }), visibility: undefined } as unknown as NewLoanValues;
    expect(issues(v)).toEqual(['visibility: loan_form.visibility_required', 'firstPaymentDate: loan_form.first_after_disbursed']);
  });

  it('checks an insurance as its mode asks, and its end after its start', () => {
    const insurance = newInsurance('2026-10-12');
    expect(issues(valid({ insurances: [insurance] }))).toEqual(['insurances.0.amount: loan_form.amount_format']);
    expect(issues(valid({ insurances: [{ ...insurance, amount: '20,50' }] }))).toEqual([]);
    expect(issues(valid({ insurances: [{ ...insurance, mode: 'rate_on_initial', rate: '100' }] }))).toEqual(['insurances.0.rate: loan_form.rate_format']);
    expect(issues(valid({ insurances: [{ ...insurance, amount: '20', endsOn: '2026-10-12' }] }))).toEqual(['insurances.0.endsOn: loan_form.ends_after_start']);
  });
});

describe('the wire', () => {
  it('sends a plain loan as one phase with its rate as a fraction, and leaves out what is empty', () => {
    expect(toCreateBody(valid())).toEqual({
      name: 'Prêt maison', lender: 'Banque', currency: 'EUR', visibility: 'private', borrowers: [{ personId: 'alice', shareBp: 10000 }],
      principal: '200000', totalPayments: 240, disbursedOn: '2026-09-12', firstPaymentDate: '2026-10-12', rounding: 'half_up',
      firstPeriod: 'full', phases: [{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed' }], insurances: [],
    });
  });

  it('numbers the phases and sends their counts, options and notes, and each insurance as its mode reads it', () => {
    const body = toCreateBody(valid({
      principal: '200000,50', note: 'résidence', rounding: 'truncate', firstPeriod: 'prorata_actual_365',
      phases: [
        { ...newPhase(), mode: 'deferred_partial', rate: '3,5', payments: '24', note: 'travaux' },
        { ...newPhase(), rate: '3.9', rateType: 'variable', paymentOverride: '1250,5', openingBalance: '190000' },
      ],
      insurances: [
        { ...newInsurance('2026-10-12'), amount: '20,5' },
        { ...newInsurance('2026-10-12'), mode: 'rate_on_remaining', rate: '0,36', endsOn: '2036-10-12', note: 'Alice' },
      ],
    }));
    expect(body).toMatchObject({ principal: '200000.50', note: 'résidence', rounding: 'truncate', firstPeriod: 'prorata_actual_365' });
    expect(body.phases).toEqual([
      { seq: 1, mode: 'deferred_partial', annualRate: '0.035', rateType: 'fixed', payments: 24, note: 'travaux' },
      { seq: 2, mode: 'annuity', annualRate: '0.039', rateType: 'variable', paymentOverride: '1250.5', openingBalanceOverride: '190000' },
    ]);
    expect(body.insurances).toEqual([
      { mode: 'fixed_monthly', monthlyAmount: '20.5', startsOn: '2026-10-12' },
      { mode: 'rate_on_remaining', annualRate: '0.0036', startsOn: '2026-10-12', endsOn: '2036-10-12', note: 'Alice' },
    ]);
  });
});

describe('server paths', () => {
  it.each([
    ['principal', 'principal'], ['firstPaymentDate', 'firstPaymentDate'], ['visibility', 'visibility'], ['rounding', 'rounding'],
    ['phases', 'totalPayments'], ['phases[0].annualRate', 'phases.0.rate'], ['phases[1].openingBalanceOverride', 'phases.1.openingBalance'],
    ['phases[1].paymentOverride', 'phases.1.paymentOverride'], ['insurances[0].monthlyAmount', 'insurances.0.amount'],
    ['insurances[2].annualRate', 'insurances.2.rate'], ['insurances[2].endsOn', 'insurances.2.endsOn'], ['borrowers', 'owners'],
    ['borrowers[0].shareBp', 'owners'],
  ])('places %s under %s', (path, field) => {
    expect(newLoanPath(path)).toBe(field);
  });

  it('leaves a path no field owns to the page', () => {
    expect(newLoanPath('phases[0].seq')).toBeUndefined();
    expect(newLoanPath('insurances')).toBeUndefined();
    expect(newLoanPath('version')).toBeUndefined();
  });
});

describe('the contract alone', () => {
  it('checks the contract’s own fields without the loan’s identity', () => {
    const { principal, totalPayments, disbursedOn, rounding, firstPeriod, phases, insurances } = valid();
    const contract = { principal, totalPayments, disbursedOn, firstPaymentDate: '2026-09-11', rounding, firstPeriod, phases, insurances };
    const r = contractSchema(t).safeParse(contract);
    expect(r.success ? [] : r.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`)).toEqual(['firstPaymentDate: loan_form.first_after_disbursed']);
  });

  it('sends the contract without the loan’s identity', () => {
    expect(toContractBody(valid())).toEqual({
      principal: '200000', totalPayments: 240, disbursedOn: '2026-09-12', firstPaymentDate: '2026-10-12', rounding: 'half_up',
      firstPeriod: 'full', phases: [{ seq: 1, mode: 'annuity', annualRate: '0.035', rateType: 'fixed' }], insurances: [],
    });
  });

  it('places a contract path, and leaves the identity’s and a repayment’s to the page', () => {
    expect(contractPath('phases[1].annualRate')).toBe('phases.1.rate');
    expect(contractPath('phases')).toBe('totalPayments');
    expect(contractPath('insurances[0].startsOn')).toBe('insurances.0.startsOn');
    expect(contractPath('name')).toBeUndefined();
    expect(contractPath('borrowers')).toBeUndefined();
    expect(contractPath('repayments[0].amount')).toBeUndefined();
  });

  it('marks an insurance added on the form as following the first payment', () => {
    expect(newInsurance('2026-10-12')).toMatchObject({ startsOn: '2026-10-12', follows: true });
  });
});
