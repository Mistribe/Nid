import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { checkOwnership, ownershipShape, ownershipToWire } from '@/components/ownership/ownership';
import { CURRENCY, ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { FIRST_PERIODS, INSURANCE_MODES, PHASE_MODES, RATE_TYPES, ROUNDINGS } from '../format';
import { isPercentRate, percentToFraction } from '../rate';

/** Spec 3B1 §4.2: the server refuses a 21st; the form stops offering one at 20. */
export const MAX_INSURANCES = 20;
const MAX_PAYMENTS = 600;

/** An amount as typed (precision 15): positive, at most two decimals, below 10¹², a comma or a dot. */
export const LOAN_AMOUNT = /^(?!0+(?:[.,]0+)?$)\d{1,12}(?:[.,]\d{1,2})?$/;

/** What a plain loan does not show of its one line, cleared when the form turns plain again (precision 11). */
export const PLAIN_HIDDEN = ['payments', 'paymentOverride', 'openingBalance', 'note'] as const;

// Counts of payments, not amounts: plain integers.
const count = (v: string) => (/^\d{1,4}$/.test(v.trim()) ? Number(v.trim()) : null);
const inRange = (v: string) => { const n = count(v); return n !== null && n >= 1 && n <= MAX_PAYMENTS; };
const optionalAmount = (v: string) => v.trim() === '' || LOAN_AMOUNT.test(v.trim());

/** The calendar month after `iso`, its day clamped to that month's end, as NodaTime's `PlusMonths` on the server (precision 12). */
export function plusOneMonth(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number) as [number, number, number];
  const [year, month]: [number, number] = m === 12 ? [y + 1, 1] : [y, m + 1];
  const last = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return `${year}-${String(month).padStart(2, '0')}-${String(Math.min(d, last)).padStart(2, '0')}`;
}

const phaseLine = (t: TFunction) => z.object({
  mode: z.enum(PHASE_MODES),
  rate: z.string().refine(isPercentRate, t('loan_form.rate_format')),
  rateType: z.enum(RATE_TYPES),
  payments: z.string().refine((v) => v.trim() === '' || inRange(v), t('loan_form.payments_range')),
  paymentOverride: z.string().refine(optionalAmount, t('loan_form.amount_format')),
  openingBalance: z.string().refine(optionalAmount, t('loan_form.amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

const insuranceLine = (t: TFunction) => z.object({
  mode: z.enum(INSURANCE_MODES),
  amount: z.string(),
  rate: z.string(),
  startsOn: z.string().regex(ISO_DATE, t('edit.required')),
  endsOn: z.string().refine((v) => v === '' || ISO_DATE.test(v), t('edit.invalid_date')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
  // 3B2 design §4.3: a line added on the form follows the first payment date until its start is typed in; a stored one does not.
  follows: z.boolean(),
});

/** The contract's fields (3B1 design §6, 3B2 design §5): the creation's and the contract page's alike. */
const contractShape = (t: TFunction) => ({
  principal: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
  totalPayments: z.string().refine(inRange, t('loan_form.duration_range')),
  disbursedOn: z.string().regex(ISO_DATE, t('edit.required')),
  firstPaymentDate: z.string().regex(ISO_DATE, t('edit.required')),
  rounding: z.enum(ROUNDINGS),
  firstPeriod: z.enum(FIRST_PERIODS),
  phases: z.array(phaseLine(t)).min(1),
  insurances: z.array(insuranceLine(t)),
});

const contractObject = (t: TFunction) => z.object(contractShape(t));
export type ContractValues = z.infer<ReturnType<typeof contractObject>>;

/** The contract's cross-field checks, mirroring the server's (3B1 design §6). */
function checkContract(v: ContractValues, ctx: z.RefinementCtx, t: TFunction): void {
  // ISO dates compare as strings.
  if (v.firstPaymentDate < v.disbursedOn)
    ctx.addIssue({ code: 'custom', path: ['firstPaymentDate'], message: t('loan_form.first_after_disbursed') });
  if (v.phases.length > 1) {
    v.phases.forEach((p, i) => {
      if (i < v.phases.length - 1 && p.payments.trim() === '')
        ctx.addIssue({ code: 'custom', path: ['phases', i, 'payments'], message: t('loan_form.payments_required') });
    });
    const counted = v.phases.reduce((sum, p) => sum + (count(p.payments) ?? 0), 0);
    if (counted > (count(v.totalPayments) ?? 0))
      ctx.addIssue({ code: 'custom', path: ['totalPayments'], message: t('loan_form.phases_exceed_duration') });
  }
  v.insurances.forEach((ins, i) => {
    if (ins.mode === 'fixed_monthly' && !LOAN_AMOUNT.test(ins.amount.trim()))
      ctx.addIssue({ code: 'custom', path: ['insurances', i, 'amount'], message: t('loan_form.amount_format') });
    if (ins.mode !== 'fixed_monthly' && !isPercentRate(ins.rate))
      ctx.addIssue({ code: 'custom', path: ['insurances', i, 'rate'], message: t('loan_form.rate_format') });
    if (ins.endsOn !== '' && ins.endsOn <= ins.startsOn)
      ctx.addIssue({ code: 'custom', path: ['insurances', i, 'endsOn'], message: t('loan_form.ends_after_start') });
  });
}

/** The contract page's schema (3B2 design §5). */
export function contractSchema(t: TFunction) {
  return contractObject(t).superRefine((v, ctx) => checkContract(v, ctx, t));
}

/** Spec §6's client checks, mirroring the server's: the loan's identity, then its contract. */
export function newLoanSchema(t: TFunction) {
  return z.object({
    name: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
    lender: z.string().max(100, t('loan_form.lender_too_long')),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('field_error.request_currency')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
    // No default (spec §6): the person answers, or nothing is sent.
    // Not aborting: an aborting issue would skip the superRefine below and hide its checks until a second submit.
    visibility: z.custom<'shared' | 'private'>((x) => x === 'shared' || x === 'private', { error: t('loan_form.visibility_required'), abort: false }),
    owners: ownershipShape(t).owners,
    propertyId: z.string(),
    ...contractShape(t),
  }).superRefine((v, ctx) => {
    checkOwnership(v, ctx, t);
    checkContract(v, ctx, t);
  });
}

export type NewLoanValues = z.infer<ReturnType<typeof newLoanSchema>>;
export type PhaseLine = ContractValues['phases'][number];
export type InsuranceLine = ContractValues['insurances'][number];

export function newPhase(): PhaseLine {
  return { mode: 'annuity', rate: '', rateType: 'fixed', payments: '', paymentOverride: '', openingBalance: '', note: '' };
}

/** An insurance added on the form: it starts with the loan's first payment and follows it (3B2 design §4.3). */
export function newInsurance(startsOn: string): InsuranceLine {
  return { mode: 'fixed_monthly', amount: '', rate: '', startsOn, endsOn: '', note: '', follows: true };
}

/** The contract as both requests send it: rates as fractions by moving the point, amounts as typed with a dot, empty fields left out. */
export function toContractBody(v: ContractValues): Omit<components['schemas']['LoanContractBody'], 'version'> {
  return {
    principal: toWireAmount(v.principal), totalPayments: Number(v.totalPayments.trim()), disbursedOn: v.disbursedOn,
    firstPaymentDate: v.firstPaymentDate, rounding: v.rounding, firstPeriod: v.firstPeriod,
    phases: v.phases.map((p, i) => ({
      seq: i + 1, mode: p.mode, annualRate: percentToFraction(p.rate) ?? p.rate, rateType: p.rateType,
      ...(p.payments.trim() ? { payments: Number(p.payments.trim()) } : {}),
      ...(p.paymentOverride.trim() ? { paymentOverride: toWireAmount(p.paymentOverride) } : {}),
      ...(p.openingBalance.trim() ? { openingBalanceOverride: toWireAmount(p.openingBalance) } : {}),
      ...(p.note.trim() ? { note: p.note } : {}),
    })),
    insurances: v.insurances.map((ins) => ({
      mode: ins.mode,
      ...(ins.mode === 'fixed_monthly' ? { monthlyAmount: toWireAmount(ins.amount) } : { annualRate: percentToFraction(ins.rate) ?? ins.rate }),
      startsOn: ins.startsOn,
      ...(ins.endsOn ? { endsOn: ins.endsOn } : {}),
      ...(ins.note.trim() ? { note: ins.note } : {}),
    })),
  };
}

/** The one creation request (spec §6): the loan's identity, then its contract. */
export function toCreateBody(v: NewLoanValues): components['schemas']['CreateLoanBody'] {
  const { visibility, owners } = ownershipToWire(v);
  return {
    name: v.name.trim(), currency: v.currency, visibility, borrowers: owners,
    ...(v.lender.trim() ? { lender: v.lender.trim() } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    ...(v.propertyId ? { propertyId: v.propertyId } : {}),
    ...toContractBody(v),
  };
}

const CONTRACT_TOP: Record<string, Path<ContractValues>> = {
  principal: 'principal', totalPayments: 'totalPayments', disbursedOn: 'disbursedOn', firstPaymentDate: 'firstPaymentDate',
  rounding: 'rounding', firstPeriod: 'firstPeriod',
  // More payments in the phases than in the loan, or a schedule too long: both about the duration (precision 14).
  phases: 'totalPayments',
};
const IDENTITY_TOP: Record<string, Path<NewLoanValues>> = {
  name: 'name', lender: 'lender', currency: 'currency', note: 'note', visibility: 'visibility', borrowers: 'owners', propertyId: 'propertyId',
};
const PHASE: Record<string, keyof PhaseLine> = {
  mode: 'mode', annualRate: 'rate', rateType: 'rateType', payments: 'payments', paymentOverride: 'paymentOverride',
  openingBalanceOverride: 'openingBalance', note: 'note',
};
const INSURANCE: Record<string, keyof InsuranceLine> = {
  mode: 'mode', monthlyAmount: 'amount', annualRate: 'rate', startsOn: 'startsOn', endsOn: 'endsOn', note: 'note',
};

/** A contract path (`principal`, `phases[1].annualRate`, `insurances[0].endsOn`) to the form field that owns it. */
export function contractPath(path: string): Path<ContractValues> | undefined {
  if (Object.hasOwn(CONTRACT_TOP, path)) return CONTRACT_TOP[path];
  const line = /^(phases|insurances)\[(\d+)\]\.(\w+)$/.exec(path);
  if (!line) return undefined;
  const field = line[1] === 'phases' ? PHASE[line[3]!] : INSURANCE[line[3]!];
  return field ? (`${line[1]}.${Number(line[2])}.${field}` as Path<ContractValues>) : undefined;
}

/** A creation path: the loan's identity (`name`, `borrowers[0].shareBp`), or its contract's. */
export function newLoanPath(path: string): Path<NewLoanValues> | undefined {
  if (Object.hasOwn(IDENTITY_TOP, path)) return IDENTITY_TOP[path];
  if (path.startsWith('borrowers[')) return 'owners';
  return contractPath(path);
}
