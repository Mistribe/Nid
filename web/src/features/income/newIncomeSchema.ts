import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { checkOwnership, ownershipPath, ownershipShape, ownershipToWire } from '@/components/ownership/ownership';
import { CURRENCY } from '../subscriptions/edits/amount';
import { KINDS } from './format';
import { checkPhase, overlappingLines, phaseFieldOf, phaseShape, toWirePhase } from './phases/phase';
import '@/locales/income';
import '@/locales/investments';

export function newIncomeSchema(t: TFunction) {
  return z.object({
    displayName: z.string().trim().min(1, t('edit.required')).max(100, t('investments_form.name_too_long')),
    kind: z.enum(KINDS),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('field_error.request_currency')),
    payerLabel: z.string().max(100, t('income_form.payer_too_long')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
    // No default (spec §5): the person answers, or nothing is sent.
    visibility: z.enum(['shared', 'private'], { error: t('income_form.visibility_required') }),
    owners: ownershipShape(t).owners,
    phases: z.array(phaseShape(t)),
  }).superRefine((v, ctx) => {
    checkOwnership(v, ctx, t);
    v.phases.forEach((p, i) => checkPhase(p, ctx, t, ['phases', i]));
    for (const i of overlappingLines(v.phases))
      ctx.addIssue({ code: 'custom', path: ['phases', i, 'startsOn'], message: t('field_error.income_phase_overlap') });
  });
}

export type NewIncomeValues = z.infer<ReturnType<typeof newIncomeSchema>>;

const TOP = ['displayName', 'kind', 'currency', 'payerLabel', 'note'] as const;

/** A server path (`phases[1].startsOn`, `owners[0].shareBp`, `payerLabel`) to the form field that owns it. */
export function newIncomePath(path: string): Path<NewIncomeValues> | undefined {
  const top = TOP.find((f) => f === path);
  if (top) return top;
  const line = /^phases\[(\d+)\]\.(.+)$/.exec(path);
  if (line) {
    const field = phaseFieldOf(line[2]!);
    return field ? (`phases.${Number(line[1])}.${field}` as Path<NewIncomeValues>) : undefined;
  }
  return ownershipPath(path);
}

export function toCreateBody(v: NewIncomeValues): components['schemas']['CreateIncomeSourceBody'] {
  return {
    displayName: v.displayName, kind: v.kind, currency: v.currency, ...ownershipToWire(v),
    ...(v.payerLabel.trim() ? { payerLabel: v.payerLabel.trim() } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
    phases: v.phases.map((p) => toWirePhase(p, v.currency)),
  };
}
