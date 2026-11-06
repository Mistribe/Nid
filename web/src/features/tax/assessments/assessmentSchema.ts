import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import { AMOUNT, toFieldAmount, toWireAmount } from '../../subscriptions/edits/amount';
import type { TaxAssessment, TaxAssessmentBody } from './api';
import '@/locales/tax';
import '@/locales/taxAssessments';

/** Money fields of a declarant, then of the notice, in the order the form shows them (the net salaries are the server's). */
export const DECLARANT_AMOUNTS = ['grossSalaries', 'deductionAmount'] as const;
export const PER_AMOUNTS = ['perCeilingYear', 'perUnusedOldest', 'perUnusedMiddle', 'perUnusedLatest', 'perCeilingComputed', 'perCeilingNext'] as const;
/** The year each PER line names on the notice of the revenue year `year` (revenus 2025: total 2024, unused 2023–2025, next 2026). */
const PER_YEAR_OFFSET: Record<(typeof PER_AMOUNTS)[number], number> = {
  perCeilingYear: -1, perUnusedOldest: -2, perUnusedMiddle: -1, perUnusedLatest: 0, perCeilingComputed: 0, perCeilingNext: 1,
};
export const perLineYear = (key: (typeof PER_AMOUNTS)[number], year: string | number) => Number(year) + PER_YEAR_OFFSET[key];
export const isPerAmount = (key: string): key is (typeof PER_AMOUNTS)[number] => (PER_AMOUNTS as readonly string[]).includes(key);
/** A declarant's detail lines: heures supplémentaires, then the withholding and the January advance. */
export const DECLARANT_DETAIL = ['overtimeExempt', 'withholding', 'creditsAdvance'] as const;
export const NOTICE_AMOUNTS = {
  household: ['referenceIncome'],
  income: ['grossIncome', 'taxableIncome', 'capitalIncomeDeclared', 'capitalIncomeTaxable', 'dividendsEligible', 'capitalIncomeAlreadyLevied', 'rentalIncomeNet', 'flatRateIncome'],
  incomeFolded: ['exemptIncomeDeclared', 'exemptIncomeNet'],
  tax: ['scaleTax', 'proportionalTax', 'taxBeforeCredits', 'flatRatePrepaidCredit', 'pinelReduction', 'childcareExpenses', 'creditsDeclared', 'creditsRetained', 'netTax', 'withholding'],
  carried: ['rentalDeficitOnRentalIncome', 'rentalDeficitOnGlobalIncome', 'rentalDeficitCarried'],
} as const;
const AMOUNT_FIELDS = [...NOTICE_AMOUNTS.household, ...NOTICE_AMOUNTS.income, ...NOTICE_AMOUNTS.incomeFolded, ...NOTICE_AMOUNTS.tax, 'balance', ...NOTICE_AMOUNTS.carried] as const;
export const RATES = ['averageRate', 'marginalRate'] as const;

/** The PER lines the form asks for: the ceiling for next year's contributions is the server's sum of the others. */
type PerTyped = Exclude<(typeof PER_AMOUNTS)[number], 'perCeilingNext'>;
export const PER_TYPED = PER_AMOUNTS.filter((k): k is PerTyped => k !== 'perCeilingNext');
type DeclarantAmount = (typeof DECLARANT_AMOUNTS)[number] | PerTyped | (typeof DECLARANT_DETAIL)[number];
/** Every money field of a declarant the form asks for. */
const DECLARANT_MONEY = [...DECLARANT_AMOUNTS, ...DECLARANT_DETAIL, ...PER_TYPED];
export type DeclarantValues = { personId: string; deduction: '' | 'allowance' | 'actual' } & Record<DeclarantAmount, string>;
/** Every amount is the text the person typed (`''` is « not on the notice »); `balanceSign` only chooses the sign the body carries. */
export type AssessmentValues = {
  year: string; issuedYear: string; declarants: DeclarantValues[]; shares: string;
  balanceSign: 'due' | 'refund'; documentId: string;
} & Record<(typeof AMOUNT_FIELDS)[number] | (typeof RATES)[number], string>;

export type StepId = 'household' | 'declarants' | 'income' | 'tax' | 'source';

/** The step that owns a form field (`declarants.1.personId` is the household's, the declarant's figures the next step's). */
export function stepOf(name: string): StepId {
  const declarant = /^declarants\.\d+\.(\w+)$/.exec(name);
  if (declarant) return declarant[1] === 'personId' ? 'household' : 'declarants';
  if (['year', 'issuedYear', 'shares', 'averageRate', 'marginalRate', ...NOTICE_AMOUNTS.household].includes(name)) return 'household';
  if ((NOTICE_AMOUNTS.income as readonly string[]).concat(NOTICE_AMOUNTS.incomeFolded).includes(name)) return 'income';
  if (name === 'balance' || name === 'balanceSign' || (NOTICE_AMOUNTS.tax as readonly string[]).includes(name)) return 'tax';
  return 'source';
}

/** The fields a step holds, for « Continuer » to validate. */
export function stepFields(id: StepId, declarants: number): Path<AssessmentValues>[] {
  const own = (n: number) => Array.from({ length: n }, (_, i) => i);
  const perDeclarant = (keys: readonly string[]) => own(declarants).flatMap((i) => keys.map((k) => `declarants.${i}.${k}`));
  const all = {
    household: ['year', 'issuedYear', ...perDeclarant(['personId']), 'shares', ...NOTICE_AMOUNTS.household, ...RATES],
    declarants: perDeclarant(DECLARANT_MONEY),
    income: [...NOTICE_AMOUNTS.income, ...NOTICE_AMOUNTS.incomeFolded],
    tax: [...NOTICE_AMOUNTS.tax, 'balance'],
    source: ['documentId', ...NOTICE_AMOUNTS.carried],
  } satisfies Record<StepId, string[]>;
  return all[id] as Path<AssessmentValues>[];
}

/** A server path (`declarants[1].personId`, `netTax.amount`) to the form field that owns it. */
export function assessmentPath(path: string): Path<AssessmentValues> | undefined {
  const row = /^declarants\[(\d+)\]\.(\w+)/.exec(path);
  if (row) return [...DECLARANT_MONEY, 'personId', 'deduction'].includes(row[2]!) ? `declarants.${Number(row[1])}.${row[2]}` as Path<AssessmentValues> : undefined;
  if (path === 'declarants') return 'declarants.0.personId';
  const top = path.replace(/\.(amount|currency)$/, '');
  return ['year', 'issuedYear', 'shares', 'documentId', ...AMOUNT_FIELDS, ...RATES].includes(top) ? top as Path<AssessmentValues> : undefined;
}

export function assessmentSchema(t: TFunction, adults: number) {
  const amount = z.string().trim().refine((v) => v === '' || AMOUNT.test(v.replace(/\s/g, '')), t('tax.amount_format'));
  // A rental deficit: the notice prints the net rental income negative.
  const signedAmount = z.string().trim().refine((v) => amount.safeParse(v.replace(/^-(?=.)/, '')).success, t('tax.amount_format'));
  const declarant = z.object({
    personId: z.string(), deduction: z.enum(['', 'allowance', 'actual']),
    ...Object.fromEntries(DECLARANT_MONEY.map((k) => [k, amount])),
  });
  return z.object({
    year: z.string().min(1, t('tax.assessment_form.error.year_required')),
    issuedYear: z.string().trim().refine((v) => v === '' || /^\d{4}$/.test(v), t('tax.assessment_form.error.issued_year_format')),
    declarants: z.array(declarant), shares: amount,
    balanceSign: z.enum(['due', 'refund']), documentId: z.string(),
    ...Object.fromEntries([...AMOUNT_FIELDS, ...RATES].map((k) => [k, amount])),
    rentalIncomeNet: signedAmount,
  }).superRefine((v, ctx) => {
    if (!v.declarants[0]?.personId) ctx.addIssue({ code: 'custom', path: ['declarants', 0, 'personId'], message: t('tax.assessment_form.error.declarant_required') });
    const [first, second] = v.declarants;
    if (adults > 1 && second?.personId && second.personId === first?.personId)
      ctx.addIssue({ code: 'custom', path: ['declarants', 1, 'personId'], message: t('tax.assessment_form.error.declarant_twice') });
  });
}

export function assessmentDefaults(person: string, lastYear: number): AssessmentValues {
  const blank = Object.fromEntries([...AMOUNT_FIELDS, ...RATES].map((k) => [k, '']));
  const declarant = (personId: string): DeclarantValues => ({ personId, deduction: '', ...Object.fromEntries(DECLARANT_MONEY.map((k) => [k, ''])) } as DeclarantValues);
  return { ...blank, year: String(lastYear), issuedYear: '', shares: '', declarants: [declarant(person), declarant('')], balanceSign: 'due', documentId: '' } as AssessmentValues;
}

const money = (text: string) => (text.trim() === '' ? null : { amount: toWireAmount(text), currency: 'EUR' });

/** The request body: `''` is `null`, amounts are the typed text with a dot; the sign of the balance is a `-` on the string, never a computation. */
export function toBody(v: AssessmentValues): TaxAssessmentBody {
  const year = Number(v.year);
  const issued = Number(v.issuedYear);
  const balance = money(v.balance);
  const body = {
    year, ...(v.issuedYear.trim() && issued !== year + 1 ? { issuedYear: issued } : {}),
    // A second declarant left on « Aucun » is no declarant.
    declarants: v.declarants.filter((d) => d.personId).map((d, i) => ({
      personId: d.personId, rank: i + 1, deduction: d.deduction || null,
      ...Object.fromEntries(DECLARANT_MONEY.map((k) => [k, money(d[k])])),
    })),
    shares: v.shares.trim() ? toWireAmount(v.shares) : null,
    ...Object.fromEntries(RATES.map((k) => [k, v[k].trim() ? toWireAmount(v[k]) : null])),
    ...Object.fromEntries(AMOUNT_FIELDS.filter((k) => k !== 'balance').map((k) => [k, money(v[k])])),
    balance: balance && v.balanceSign === 'refund' && /[1-9]/.test(balance.amount) ? { ...balance, amount: `-${balance.amount}` } : balance,
    documentId: v.documentId || null,
  };
  return body;
}

const text = (m: { amount: string } | null | undefined) => toFieldAmount(m?.amount ?? '');

/** The form of a stored notice; a negative balance is « Remboursé » and its absolute text. */
export function fromView(view: TaxAssessment, persons: number): AssessmentValues {
  const base = assessmentDefaults('', Number(view.year));
  const refund = view.balance?.amount.startsWith('-') ?? false;
  const declarants = view.declarants.map((d) => ({
    ...base.declarants[0]!, personId: d.personId, deduction: d.deduction === 'allowance' || d.deduction === 'actual' ? d.deduction : '',
    ...Object.fromEntries(DECLARANT_MONEY.map((k) => [k, text(d[k])])),
  }));
  if (declarants.length < 2 && persons > 1) declarants.push(base.declarants[1]!);
  return {
    ...base, year: String(view.year), issuedYear: view.issuedYear === view.year + 1 ? '' : String(view.issuedYear),
    declarants, shares: toFieldAmount(view.shares ?? ''), averageRate: toFieldAmount(view.averageRate ?? ''), marginalRate: toFieldAmount(view.marginalRate ?? ''),
    ...Object.fromEntries(AMOUNT_FIELDS.map((k) => [k, text(view[k])])),
    balance: refund ? toFieldAmount(view.balance!.amount.slice(1)) : text(view.balance), balanceSign: refund ? 'refund' : 'due',
    documentId: view.documentId ?? '',
  } as AssessmentValues;
}
