import type { TaxAssessment } from './api';

export const ALICE = '0192f0aa-0000-7000-8000-0000000000a1';
export const BOB = '0192f0aa-0000-7000-8000-0000000000b2';
export const DOCUMENT = '0192f0aa-0000-7000-8000-0000000000d1';

const eur = (amount: string) => ({ amount, currency: 'EUR' });

/** A joint notice for 2025 incomes, issued in 2026, with Alice's PER ceilings and a refund. */
export const jointAssessment = (over: Partial<TaxAssessment> = {}): TaxAssessment => ({
  id: '0192f0aa-0000-7000-8000-0000000000e1', version: 1, year: 2025, issuedYear: 2026,
  declarants: [
    { personId: ALICE, rank: 1, grossSalaries: eur('52000.00'), deduction: 'allowance', deductionAmount: eur('5200.00'), netSalaries: eur('46800.00'),
      perCeilingYear: eur('5843.13'), perUnusedOldest: eur('1000.00'), perUnusedMiddle: null, perUnusedLatest: eur('2500.00') },
    { personId: BOB, rank: 2, grossSalaries: eur('30000.00'), overtimeExempt: eur('1250.00'), deduction: 'actual', deductionAmount: eur('4000.00'),
      netSalaries: eur('26000.00'), withholding: eur('6100.00'), creditsAdvance: eur('1250.00') },
  ],
  shares: '3', referenceIncome: eur('72800.00'), averageRate: '4.5', marginalRate: '11',
  grossIncome: eur('72800.00'), taxableIncome: eur('70000.00'),
  scaleTax: eur('3200.00'), taxBeforeCredits: eur('3200.00'), childcareExpenses: eur('1800.00'), netTax: eur('2688.00'), withholding: eur('3000.00'),
  balance: eur('-312.00'),
  documentId: DOCUMENT,
  ...over,
});

/** A solo notice: one declarant, no PDF, no balance. */
export const soloAssessment = (over: Partial<TaxAssessment> = {}): TaxAssessment => ({
  id: '0192f0aa-0000-7000-8000-0000000000e2', version: 1, year: 2024, issuedYear: 2025,
  declarants: [{ personId: ALICE, rank: 1, grossSalaries: eur('40000.00'), deduction: 'allowance', deductionAmount: eur('4000.00'), netSalaries: eur('36000.00') }],
  shares: '1', referenceIncome: eur('36000.00'), netTax: eur('2100.00'), balance: eur('150.00'), documentId: null,
  ...over,
});
