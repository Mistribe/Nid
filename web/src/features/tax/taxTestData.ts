import type { TaxEstimate, TaxResult } from './api';

export const ADULT = '0192f0aa-0000-7000-8000-0000000000a1';

export const result = (over: Partial<TaxResult> = {}): TaxResult => ({
  year: 2026, rulesYear: 2025, parts: '3.0',
  declarants: [{ name: 'Alice', netSalary: '39000.00', netTaxableIncome: '40424.63', allowance: '4042.46', taxable: '36382.17', perDeducted: '0.00' }],
  rentalRegime: 'micro', rentalTaxable: '0.00', rentalDeficitCarriedUsed: '0.00', rentalDeficit: '0.00', netTaxableIncome: '64407.00', referenceIncome: '66407.00', grossTax: '3256.77', familyCapApplied: false,
  decote: '9.31', pinelReduction: '0.00', donationReduction: '0.00', childcareCredit: '3000.00', homeEmploymentCredit: '0.00', nicheCapExcess: '0.00',
  incomeTax: '247.00', capitalFlatTax: '0.00', socialLevies: '0.00', totalTax: '247.00', marginalRate: '11', averageRate: '0.38',
  netBeforeTax: '69000.00', netAfterTax: '68753.00', netAfterTaxMonthly: '5729.42', warnings: ['rules_carried_forward'],
  withholding: {
    rate: '4.5', taxBase: '3247.00', income: '40424.63', declarants: [{ name: 'Alice', salaries: '40424.63', monthly: '151.59' }],
    rentalDeclared: '0.00', rentalMonthly: '0.00', annual: '3220.39', leftOut: '3000.00', balance: '-2973.39',
  },
  ...over,
});

export const estimate = (): TaxEstimate => ({
  year: 2026, profileFrom: null, dependents: 2, rentalProperties: [],
  profile: { statuses: {}, dependents: null, childrenUnder6: 2, rentalRegime: null, pinel: [], childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00', donationsAid: '0.00', contributionRates: {}, actualExpenses: {}, perUnusedCeilings: {}, rentalDeficitCarried: '0.00', perCeilings: {}, perCeilingsExpiring: {}, perPooling: false, exemptIncomes: {}, donationsAidLate: '0.00', rentalEnergyWorks: '0.00' },
  inputs: {
    year: 2026, dependents: 2, childrenUnder6: 2, rentalGross: '0.00', rentalInterest: '0.00', rentalCharges: '0.00', rentalRegime: null,
    capitalIncome: '0.00', nonTaxableIncome: '0.00', pinelReduction: '0.00', childcareOutsideHome: '6000.00', homeEmployment: '0.00', donations: '0.00', donationsAid: '0.00', rentalDeficitCarried: '0.00', perPooling: false, donationsAidLate: '0.00', rentalEnergyWorks: '0.00',
    declarants: [{ name: 'Alice', salaryGross: '50000.00', status: 'non_executive', otherTaxable: '0.00', perContributions: '0.00', perUnusedCeiling: '0.00', salaryTaxable: '0.00', previousYearIncome: '0.00', perExpiring: '0.00', exemptIncome: '0.00' }],
  },
  result: result(),
  notices: [{ code: 'missing_gross', item: 'b', name: 'Prime', amount: '1000.00' }],
  adults: [{ id: ADULT, displayName: 'Alice' }],
  salaryDetail: [], observedRates: {},
});
