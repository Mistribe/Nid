import type { ChildcareDetail, ChildcareList, ChildcareSummary, ChildcareTerms, ChildcareYear, Statement, StatementDay, StatementList, WeekdaySlot } from './api';

export const eur = (amount: string) => ({ amount, currency: 'EUR' });

export const ARRANGEMENT_ID = '0192f0aa-0000-7000-8000-00000000c001';
export const CHILD = '0192f0aa-0000-7000-8000-00000000d001';

export const slot = (day: number, over: Partial<WeekdaySlot> = {}): WeekdaySlot => ({ day, arrival: '08:00', departure: '17:30', lunch: true, snack: false, ...over });

export const summary = (over: Partial<ChildcareSummary> = {}): ChildcareSummary => ({
  id: ARRANGEMENT_ID, name: 'Garde de Léa', childId: CHILD, childName: 'Léa', carerName: 'Martine', owners: [], visibility: 'shared',
  closed: false, canWrite: true, start: '2025-09-01', end: null, inForceFrom: '2025-09-01', monthlyBasePay: eur('626.37'), weeklyHours: '38.00', ...over,
});

export const list = (items: ChildcareSummary[]): ChildcareList => ({ items });

export const terms = (over: Partial<ChildcareTerms> = {}): ChildcareTerms => ({
  from: '2025-09-01', to: null, netHourlyRate: '4.30', grossHourlyRate: null, overtimePct: '10', upkeepPer9h: '3.80', lunchPrice: '3.2', snackPrice: null, lunchProvider: 'nanny', snackProvider: 'nanny',
  weekTypes: [{ name: 'Semaine type', weeks: 46, schedule: [1, 2, 4, 5].map((d) => slot(d)), weeklyHours: '38.00' }], programmedWeeks: 46,
  monthlyBasePay: eur('626.37'), monthlyHours: '151.67', normalHours: 146, ...over,
});

export const detail = (over: Partial<ChildcareDetail> = {}, head: Partial<ChildcareSummary> = {}): ChildcareDetail => ({
  summary: summary(head), carerPajemploi: 'P123', carerApproval: null, terms: [terms()], gaps: [], gapWeeksToPlace: 6, leaveDaysAccrued: 3, version: 7, weeks: [], allocation: [], ...over,
});

/** Two template weeks over three Mondays: the first on A, the second overridden to B, the third without care. */
export const twoWeekTypes = (): ChildcareDetail => detail({
  terms: [terms({
    weekTypes: [
      { name: 'Semaine longue', weeks: 23, schedule: [1, 2, 4, 5].map((d) => slot(d)), weeklyHours: '39.00' },
      { name: 'Semaine courte', weeks: 23, schedule: [1, 2].map((d) => slot(d)), weeklyHours: '19.00' },
    ], programmedWeeks: 46,
  })],
  weeks: [
    { monday: '2025-09-01', weekType: 0, overridden: false, gap: false },
    { monday: '2025-09-08', weekType: 1, overridden: true, gap: false },
    { monday: '2025-09-15', weekType: 0, overridden: false, gap: true },
  ],
  allocation: [{ name: 'Semaine longue', allocated: 22, contract: 23 }, { name: 'Semaine courte', allocated: 23, contract: 23 }],
});

/** September 2025 as the template plans it: Mon, Tue, Thu, Fri 08:00–17:30 with meal. */
export const september = (): StatementDay[] => Array.from({ length: 30 }, (_, i) => {
  const date = `2025-09-${String(i + 1).padStart(2, '0')}`;
  const planned = [0, 1, 3, 4].includes(i % 7); // 1 September 2025 is a Monday
  return planned
    ? { date, planned, present: true, arrival: '08:00', departure: '17:30', lunch: true, snack: false, absence: null, templateArrival: '08:00', templateDeparture: '17:30', changed: false, weekType: null }
    : { date, planned, present: false, arrival: null, departure: null, lunch: false, snack: false, absence: null, templateArrival: null, templateDeparture: null, changed: false, weekType: null };
});

export const statement = (over: Partial<Statement> = {}): Statement => ({
  id: ARRANGEMENT_ID, name: 'Garde de Léa', month: '2025-09', state: 'draft', days: september(), mileage: '0.00', cmg: null,
  figures: {
    declaration: { normalHours: 146, activityDays: 16, paidLeaveDays: null, complementaryHours: '0.00', overtimeHours: '0.00', netPay: '626.37',
      upkeep: '70.40', meals: '57.60', mileage: '0.00' },
    basePay: eur('626.37'), deduction: eur('0.00'), extraPay: eur('0.00'), paidLeavePay: eur('0.00'), cost: eur('754.37'), costBeforeCmg: true,
  },
  problems: [], missingReferenceMonths: [], canWrite: true, canReopen: false, version: 3, ...over,
});

export const statements = (): StatementList => ({ id: ARRANGEMENT_ID, months: [
  { month: '2025-10', state: 'none', cost: null },
  { month: '2025-09', state: 'validated', cost: eur('660.47') },
] });

/** #411: 2025 with September to November validated, December not. */
export const yearSummary = (over: Partial<ChildcareYear> = {}): ChildcareYear => ({
  id: ARRANGEMENT_ID, name: 'Garde de Léa', childName: 'Léa', carerName: 'Martine', year: 2025, years: [2026, 2025], box: '7GA',
  netPay: eur('1879.11'), cmg: eur('361.50'), upkeep: eur('211.20'), meals: eur('172.80'), retained: eur('1728.81'),
  validatedMonths: ['2025-09', '2025-10', '2025-11'], notValidated: ['2025-12'], ...over,
});
