import type { TaxEstimate, TaxPrevious, TaxResult, TaxYear } from '@/api/tax';

export const ADULT = '0192f0aa-0000-7000-8000-0000000000a1';

export const result = (over: Partial<TaxResult> = {}): TaxResult => ({
  year: 2026, rulesYear: 2025, parts: '3.0',
  declarants: [{ name: 'Alice', netSalary: '39000.00', netTaxableIncome: '40424.63', allowance: '4042.46', taxable: '36382.17', perDeducted: '0.00', pensions: '0.00', pensionAllowance: '0.00' }],
  rentalRegime: 'micro', rentalTaxable: '0.00', rentalDeficitCarriedUsed: '0.00', rentalDeficit: '0.00', rentalDeficitCarriedForward: '0.00', alimonyDeducted: '0.00', netTaxableIncome: '64407.00', referenceIncome: '66407.00', grossTax: '3256.77', familyCapApplied: false,
  decote: '9.31', pinelReduction: '0.00', donationReduction: '0.00', childcareCredit: '3000.00', homeEmploymentCredit: '0.00', nicheCapExcess: '0.00',
  incomeTax: '247.00', capitalFlatTax: '0.00', socialLevies: '0.00', totalTax: '247.00', marginalRate: '11', averageRate: '0.38',
  netBeforeTax: '69000.00', netAfterTax: '68753.00', netAfterTaxMonthly: '5729.42', warnings: ['rules_carried_forward'],
  capitalScale: false, dividendAllowance: '0.00', deductibleCsgNextYear: '0.00', highIncomeContribution: '0.00', highIncomeSmoothed: false, realisedGainsTaxable: '0.00', capitalLossesCarriedForward: '0.00', ageDisabilityAllowance: '0.00',
  withholding: {
    rate: '4.5', taxBase: '3247.00', income: '40424.63', declarants: [{ name: 'Alice', salaries: '40424.63', monthly: '151.59', pensions: '0.00' }],
    rentalDeclared: '0.00', rentalMonthly: '0.00', annual: '3220.39', leftOut: '3000.00', balance: '-2973.39',
  },
  steps: [
    { code: 'salaries', group: 'income', amount: '71565.00', from: '0', to: '71565.00' },
    { code: 'salary_allowance', group: 'income', amount: '-7158.00', from: '71565.00', to: '64407.00' },
    { code: 'net_taxable', group: 'income', amount: '64407.00', from: '0', to: '64407.00' },
    { code: 'gross_tax', group: 'tax', amount: '3256.77', from: '0', to: '3256.77' },
    { code: 'decote', group: 'tax', amount: '-9.31', from: '3256.77', to: '3247.46' },
    { code: 'childcare', group: 'tax', amount: '-3000.00', from: '3247.46', to: '247.46' },
    { code: 'rounding', group: 'tax', amount: '-0.46', from: '247.46', to: '247.00' },
    { code: 'income_tax', group: 'tax', amount: '247.00', from: '0', to: '247.00' },
    { code: 'total_tax', group: 'tax', amount: '247.00', from: '0', to: '247.00' },
  ],
  bracket: {
    bands: [{ from: '0', to: '11497', rate: '0' }, { from: '11497', to: '29315', rate: '11' }, { from: '29315', to: '83823', rate: '30' }, { from: '83823', to: '180294', rate: '41' }, { from: '180294', to: null, rate: '45' }],
    quotient: '21469.00', nextFrom: '29315.00', toNext: '7846.00', displayEnd: '226000.00',
  },
  ...over,
});

export const estimate = (): TaxEstimate => ({
  year: 2026, profileFrom: null, dependents: 2, rentalProperties: [],
  profile: { statuses: {}, dependents: null, childrenUnder6: 2, rentalRegime: null, pinel: [], childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00', donationsAid: '0.00', contributionRates: {}, actualExpenses: {}, perUnusedCeilings: {}, rentalDeficitCarried: '0.00', perCeilings: {}, perCeilingsExpiring: {}, perPooling: false, exemptIncomes: {}, donationsAidLate: '0.00', rentalEnergyWorks: '0.00', capitalScale: false, deductibleCsg: '0.00', alimonyPaid: '0.00', alimonyAdultChildren: '0.00', alimonyAdultChildrenCount: 0, capitalLossesCarried: '0.00', alternateCustody: 0, cohabiting: false, disabledAdults: [], disabledDependents: 0, alternateUnder6: 0 },
  inputs: {
    year: 2026, dependents: 2, childrenUnder6: 2, rentalGross: '0.00', rentalInterest: '0.00', rentalCharges: '0.00', rentalRegime: null,
    capitalIncome: '0.00', nonTaxableIncome: '0.00', pinelReduction: '0.00', childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00', donationsAid: '0.00', rentalDeficitCarried: '0.00', perPooling: false, donationsAidLate: '0.00', rentalEnergyWorks: '0.00', capitalDividends: '0.00', capitalScale: false, deductibleCsg: '0.00', alimonyPaid: '0.00', alimonyAdultChildren: '0.00', alimonyAdultChildrenCount: 0, realisedGains: '0.00', capitalLossesCarried: '0.00', alternateCustody: 0, singleParent: false, disabledDependents: 0, alternateUnder6: 0,
    declarants: [{ name: 'Alice', salaryGross: '50000.00', status: 'non_executive', otherTaxable: '0.00', perContributions: '0.00', perUnusedCeiling: '0.00', salaryTaxable: '0.00', previousYearIncome: '0.00', perExpiring: '0.00', exemptIncome: '0.00', pensions: '0.00', disabled: false, over65: false }],
  },
  result: result(),
  notices: [{ code: 'missing_gross', item: 'b', name: 'Prime', amount: '1000.00' }],
  adults: [{ id: ADULT, displayName: 'Alice' }],
  salaryDetail: [], observedRates: {}, history: history(),
});

export const previous = (over: Partial<TaxPrevious> = {}): TaxPrevious => ({ year: 2025, source: 'assessment', tax: '7000.00', change: '360.00', changePct: '5.1', ...over });

export const history = (): TaxYear[] => [
  { year: 2024, source: 'assessment', tax: '3100.00', netTaxable: '60000.00', averageRate: '4.8' },
  { year: 2025, source: 'estimate', tax: '2900.00', netTaxable: '61000.00', averageRate: '4.5' },
  { year: 2026, source: 'estimate', tax: '247.00', netTaxable: '64407.00', averageRate: '0.38' },
];
