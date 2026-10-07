import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { POSITIVE_AMOUNT, trimAmount } from '../../investments/entries/entries';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import type { IncomeGap, IncomePhase } from '../api';

export const CADENCES = ['monthly', 'quarterly', 'yearly', 'months'] as const;
export type Cadence = (typeof CADENCES)[number];

const optionalAmount = (v: string) => v.trim() === '' || POSITIVE_AMOUNT.test(v.trim());
// A count of occurrences, not an amount: `Number` is fine here.
const cycles = (v: string) => v.trim() === '' || (/^\d+$/.test(v.trim()) && Number(v.trim()) >= 1 && Number(v.trim()) <= 1200);

/** One phase's fields as typed (2C1 spec §2's bounds); `checkPhase` adds the rules across fields. */
export function phaseShape(t: TFunction) {
  return z.object({
    label: z.string().trim().min(1, t('edit.required')).max(60, t('income_form.label_too_long')),
    startsOn: z.string().regex(ISO_DATE, t('edit.required')),
    endsOn: z.string().refine((v) => v === '' || ISO_DATE.test(v), t('edit.invalid_date')),
    netAmount: z.string().trim().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    cadence: z.enum(CADENCES),
    months: z.string(),
    grossAmount: z.string().refine(optionalAmount, t('investments_form.positive_amount_format')),
    maxCycles: z.string().refine(cycles, t('income_form.max_cycles_range')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
}

export type PhaseValues = z.infer<ReturnType<typeof phaseShape>>;

/** The rules across fields; `at` is the line's path in a larger form (`['phases', 1]`). */
export function checkPhase(v: PhaseValues, ctx: z.RefinementCtx, t: TFunction, at: (string | number)[] = []): void {
  // ISO dates compare as strings.
  if (ISO_DATE.test(v.startsOn) && ISO_DATE.test(v.endsOn) && v.endsOn <= v.startsOn)
    ctx.addIssue({ code: 'custom', path: [...at, 'endsOn'], message: t('income_form.ends_after_start') });
  checkMonths(v, ctx, t, at);
}

/** « Tous les N mois » needs N between 2 and 12; `at` is the line's path in a larger form. */
export function checkMonths(v: Pick<PhaseValues, 'cadence' | 'months'>, ctx: z.RefinementCtx, t: TFunction, at: (string | number)[] = []): void {
  if (v.cadence === 'months' && !/^(?:[2-9]|1[0-2])$/.test(v.months.trim()))
    ctx.addIssue({ code: 'custom', path: [...at, 'months'], message: t('income_form.months_range') });
}

export const phaseSchema = (t: TFunction) => phaseShape(t).superRefine((v, ctx) => checkPhase(v, ctx, t));

const NO_END = '9999-12-31';

/**
 * Spec §5 and §10.5, as the server checks a creation: each line against the ones before it, same trimmed label, each up
 * to its end; the later line is refused. A line capped by its occurrences is left to the server: its effective end
 * needs calendar arithmetic the browser does not do.
 */
export function overlappingLines(lines: PhaseValues[]): number[] {
  const comparable = (l: PhaseValues) => l.maxCycles.trim() === '' && ISO_DATE.test(l.startsOn) && (l.endsOn === '' || ISO_DATE.test(l.endsOn));
  const end = (l: PhaseValues) => (l.endsOn === '' ? NO_END : l.endsOn);
  const refused: number[] = [];
  lines.forEach((later, j) => {
    if (!comparable(later)) return;
    const clash = lines.slice(0, j).some((earlier) => comparable(earlier) && earlier.label.trim() === later.label.trim()
      && earlier.startsOn < end(later) && later.startsOn < end(earlier));
    if (clash) refused.push(j);
  });
  return refused;
}

export function cadenceToWire(cadence: Cadence, months: string): { count: number; unit: 'month' | 'year' } {
  switch (cadence) {
    case 'monthly': return { count: 1, unit: 'month' };
    case 'quarterly': return { count: 3, unit: 'month' };
    case 'yearly': return { count: 1, unit: 'year' };
    case 'months': return { count: Number(months.trim()), unit: 'month' };
  }
}

export function cadenceFromWire(every: { count: number; unit: string }): { cadence: Cadence; months: string } {
  if (every.unit === 'year') return { cadence: 'yearly', months: '' };
  if (every.count === 1) return { cadence: 'monthly', months: '' };
  if (every.count === 3) return { cadence: 'quarterly', months: '' };
  return { cadence: 'months', months: String(every.count) };
}

/** A phase as the API reads it, in the source's currency; an empty optional field is left out. */
export function toWirePhase(v: PhaseValues, currency: string): components['schemas']['IncomePhaseBody'] {
  const money = (text: string) => ({ amount: toWireAmount(text), currency });
  return {
    label: v.label.trim(), startsOn: v.startsOn, netAmount: money(v.netAmount), every: cadenceToWire(v.cadence, v.months),
    ...(v.endsOn ? { endsOn: v.endsOn } : {}),
    ...(v.grossAmount.trim() ? { grossAmount: money(v.grossAmount) } : {}),
    ...(v.maxCycles.trim() ? { maxCycles: Number(v.maxCycles.trim()) } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
  };
}

export function newPhase(label = '', startsOn = todayIso()): PhaseValues {
  return { label, startsOn, endsOn: '', netAmount: '', cadence: 'monthly', months: '', grossAmount: '', maxCycles: '', note: '' };
}

export function phaseValuesOf(p: IncomePhase): PhaseValues {
  return {
    label: p.label, startsOn: p.startsOn, endsOn: p.endsOn ?? '', netAmount: trimAmount(p.netAmount.amount), ...cadenceFromWire(p.every),
    grossAmount: p.grossAmount ? trimAmount(p.grossAmount.amount) : '', maxCycles: p.maxCycles == null ? '' : String(p.maxCycles),
    note: p.note ?? '',
  };
}

/** « Renseigner la suite » (spec §7): the same label, starting where the ending phase ends, same cadence and net amount. */
export function followingPhase(gap: IncomeGap, phases: IncomePhase[]): PhaseValues {
  const ending = phases.filter((p) => p.label === gap.label).sort((a, b) => (a.startsOn < b.startsOn ? 1 : a.startsOn > b.startsOn ? -1 : 0))[0];
  return {
    ...newPhase(gap.label, gap.endsOn),
    ...(ending ? { netAmount: trimAmount(ending.netAmount.amount), ...cadenceFromWire(ending.every) } : {}),
  };
}

const PHASE_FIELDS = ['label', 'startsOn', 'endsOn', 'netAmount', 'grossAmount', 'maxCycles', 'note'] as const;

/** A server path inside one phase (`netAmount.amount`, `every.count`, `startsOn`) to the form field that owns it. */
export function phaseFieldOf(path: string): keyof PhaseValues | undefined {
  const head = path.split('.')[0] ?? '';
  if (head === 'every') return 'cadence';
  return PHASE_FIELDS.find((f) => f === head);
}

/** Spec §3: the change of amount's fields — the phase's amounts and cadence, and the effective date chosen from a list. */
export function phaseChangeSchema(t: TFunction) {
  return phaseShape(t).pick({ netAmount: true, cadence: true, months: true, grossAmount: true, note: true })
    .extend({ effectiveOn: z.string().regex(ISO_DATE, t('edit.required')) })
    .superRefine((v, ctx) => checkMonths(v, ctx, t));
}

export type PhaseChangeValues = z.infer<ReturnType<typeof phaseChangeSchema>>;

/** The phase's current amounts and cadence; the note starts empty (the old one may not apply to the next phase). */
export function phaseChangeValuesOf(p: IncomePhase, effectiveOn: string): PhaseChangeValues {
  const v = phaseValuesOf(p);
  return { effectiveOn, netAmount: v.netAmount, cadence: v.cadence, months: v.months, grossAmount: v.grossAmount, note: '' };
}

export function toWirePhaseChange(label: string, v: PhaseChangeValues, currency: string): components['schemas']['IncomePhaseChangeBody'] {
  const money = (text: string) => ({ amount: toWireAmount(text), currency });
  return {
    label, effectiveOn: v.effectiveOn, netAmount: money(v.netAmount), every: cadenceToWire(v.cadence, v.months),
    ...(v.grossAmount.trim() ? { grossAmount: money(v.grossAmount) } : {}),
    ...(v.note.trim() ? { note: v.note } : {}),
  };
}

const CHANGE_FIELDS = ['netAmount', 'cadence', 'grossAmount', 'note'] as const;

/** A server path of a change to the field that owns it; `label` belongs to no field (the refusal shows above the form). */
export function phaseChangeFieldOf(path: string): keyof PhaseChangeValues | undefined {
  if (path === 'effectiveOn') return 'effectiveOn';
  const field = phaseFieldOf(path);
  return CHANGE_FIELDS.find((f) => f === field);
}

const RANK: Record<string, number> = { running: 0, upcoming: 1, past: 2 };

/** Spec §7 and §10: one group per label, in French order; within it running, upcoming, then past, newest first. */
export function groupPhases(phases: IncomePhase[]): { label: string; phases: IncomePhase[] }[] {
  const labels = [...new Set(phases.map((p) => p.label))].sort((a, b) => a.localeCompare(b, 'fr'));
  return labels.map((label) => ({
    label,
    phases: phases.filter((p) => p.label === label).sort((a, b) =>
      (RANK[a.status] ?? 3) - (RANK[b.status] ?? 3) || (a.startsOn < b.startsOn ? 1 : a.startsOn > b.startsOn ? -1 : 0)),
  }));
}
