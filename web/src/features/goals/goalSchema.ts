import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import { ownershipPath, ownershipShape, checkOwnership, ownershipToWire, percentToBp, type OwnerRow } from '@/components/ownership/ownership';
import { AMOUNT, ISO_DATE, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import type { CreateGoalBody, EditGoalBody, GoalDetail } from '@/api/goals';
import '@/locales/goals';
import '@/locales/forms';

export const KINDS = ['savings', 'emergency_fund'] as const;
export const DEFAULT_MONTHS = '6';

/** The form of a goal, for the creation page and the edit dialog (which leaves `kind`, `visibility` and `owners` as they are). */
export type GoalValues = {
  kind: (typeof KINDS)[number]; name: string; amount: string; months: string; targetDate: string; note: string;
  mode: 'envelopes' | 'free'; funding: { envelopeId: string; share: string }[];
  visibility: 'shared' | 'private'; owners: OwnerRow[]; shareLevel?: 'view' | 'edit';
};

/**
 * `withOwners` is the creation page; `keepDate` is the date an edited goal already has, which stays valid once past;
 * `hiddenLinks` is an edited goal funded by envelopes its editor cannot see (the server keeps them), so no visible row is needed.
 */
export function goalSchema(t: TFunction, { withOwners, keepDate, hiddenLinks }: { withOwners: boolean; keepDate?: string; hiddenLinks?: boolean }) {
  const base = z.object({
    kind: z.enum(KINDS),
    name: z.string().trim().min(1, t('edit.required')).max(100, t('goals_form.error.name_too_long')),
    amount: z.string(), months: z.string(), targetDate: z.string().trim(),
    note: z.string().max(2000, t('goals_form.error.note_too_long')),
    mode: z.enum(['envelopes', 'free']),
    funding: z.array(z.object({ envelopeId: z.string(), share: z.string() })),
  });
  return (withOwners ? base.extend(ownershipShape(t)) : base).superRefine((v, ctx) => {
    const add = (path: (string | number)[], key: 'amount_format' | 'months_range' | 'date_format' | 'date_past' | 'funding_required' | 'envelope_required' | 'envelope_twice' | 'share_format') => ctx.addIssue({ code: 'custom', path, message: t(`goals_form.error.${key}`) });
    if (v.kind === 'savings' && !AMOUNT.test(v.amount.trim())) add(['amount'], 'amount_format');
    if (v.kind === 'emergency_fund' && !(/^\d{1,2}$/.test(v.months.trim()) && Number(v.months) >= 1 && Number(v.months) <= 24)) add(['months'], 'months_range');
    if (v.targetDate) {
      if (!ISO_DATE.test(v.targetDate)) add(['targetDate'], 'date_format');
      else if (v.targetDate < todayIso() && v.targetDate !== keepDate) add(['targetDate'], 'date_past');
    }
    if (v.mode === 'envelopes') {
      if (v.funding.length === 0 && !hiddenLinks) add(['funding'], 'funding_required');
      v.funding.forEach((row, i) => {
        if (!row.envelopeId) add(['funding', i, 'envelopeId'], 'envelope_required');
        else if (v.funding.findIndex((r) => r.envelopeId === row.envelopeId) !== i) add(['funding', i, 'envelopeId'], 'envelope_twice');
        const bp = percentToBp(row.share);
        if (bp === null || bp === 0) add(['funding', i, 'share'], 'share_format');
      });
    }
    if (withOwners) checkOwnership(v as unknown as { visibility: 'shared' | 'private'; owners: OwnerRow[] }, ctx, t);
  });
}

/** A server path (`funding[1].shareBp`, `targetAmount`) to the form field that owns it. */
export function goalPath(path: string): Path<GoalValues> | undefined {
  const top = { name: 'name', note: 'note', months: 'months', targetDate: 'targetDate', targetAmount: 'amount', kind: 'kind' } as const;
  if (Object.hasOwn(top, path)) return top[path as keyof typeof top];
  const row = /^funding\[(\d+)\]\.(envelopeId|shareBp)$/.exec(path);
  if (row) return `funding.${Number(row[1])}.${row[2] === 'shareBp' ? 'share' : 'envelopeId'}`;
  return ownershipPath(path);
}

/** Text of a share in basis points, for the field: `5000` → `50`, `3333` → `33.33` (digits only, no float). */
export function bpToPercent(bp: number): string {
  const s = String(bp).padStart(3, '0');
  return `${s.slice(0, -2)}.${s.slice(-2)}`.replace(/\.?0+$/, '');
}

function wire(v: GoalValues) {
  return {
    name: v.name.trim(),
    ...(v.kind === 'savings' ? { targetAmount: toWireAmount(v.amount) } : { months: Number(v.months) }),
    ...(v.targetDate ? { targetDate: v.targetDate } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    funding: v.mode === 'free' ? [] : v.funding.map((r) => ({ envelopeId: r.envelopeId, shareBp: percentToBp(r.share) ?? 0 })),
  };
}

export const toCreateBody = (v: GoalValues): CreateGoalBody => ({ ...wire(v), kind: v.kind, ...ownershipToWire(v) });
export const toEditBody = (v: GoalValues, version: number): EditGoalBody => ({ ...wire(v), version });

export function goalDefaults(detail?: GoalDetail): Omit<GoalValues, 'visibility' | 'owners'> {
  if (!detail) return { kind: 'savings', name: '', amount: '', months: DEFAULT_MONTHS, targetDate: '', note: '', mode: 'envelopes', funding: [{ envelopeId: '', share: '100' }] };
  const { goal } = detail;
  return {
    kind: goal.kind === 'emergency_fund' ? 'emergency_fund' : 'savings', name: goal.name, amount: toFieldAmount(goal.target?.amount ?? ''),
    months: String(detail.months ?? DEFAULT_MONTHS), targetDate: goal.targetDate ?? '', note: detail.note ?? '',
    // A partial goal without a visible envelope is still funded: envelopes, no row (the hidden links stay on the server).
    mode: detail.funding.length > 0 || goal.partial ? 'envelopes' : 'free',
    funding: detail.funding.length > 0 || goal.partial ? detail.funding.map((f) => ({ envelopeId: f.envelopeId, share: bpToPercent(f.shareBp) })) : [{ envelopeId: '', share: '100' }],
  };
}
