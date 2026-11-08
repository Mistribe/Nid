import type { TFunction } from 'i18next';
import type { Path } from 'react-hook-form';
import { z } from 'zod';
import { checkOwnership, ownershipPath, ownershipShape, ownershipToWire, type OwnerRow } from '@/components/ownership/ownership';
import { AMOUNT, ISO_DATE, toFieldAmount, toWireAmount } from '@/lib/amount';
import type { ChildcareDetail, ChildcareTerms, CreateChildcareBody, TermsBody } from './api';
import '@/locales/childcare';
import '@/locales/forms';

/** One weekday of the template: ISO day (1 = Monday), shown whether it is a day of care or not. */
export type DayRow = { day: number; on: boolean; arrival: string; departure: string; meal: boolean };
/** One template week: its name, how many weeks of the year it covers, and its weekdays. */
export type WeekTypeRow = { name: string; weeks: string; days: DayRow[] };
export type GapRow = { monday: string; weeks: string };

export type TermsValues = {
  from: string; net: string; gross: string; overtimePct: string; upkeep: string; meal: string; weekTypes: WeekTypeRow[];
};

export type ChildcareValues = TermsValues & {
  name: string; childId: string; carerName: string; carerPajemploi: string; carerApproval: string; gaps: GapRow[];
  visibility: 'shared' | 'private'; owners: OwnerRow[];
};

export const DAYS = [1, 2, 3, 4, 5, 6, 7] as const;
const TIME = /^\d{2}:\d{2}$/;
const WEEKS = /^\d{1,2}$/;

export const DEFAULT_WEEK_TYPE = 'Semaine type';

/** The seven weekdays of a template: the slots given are checked, the others kept off with the usual hours. */
export const daysOf = (schedule?: { day: number; arrival: string; departure: string; meal: boolean }[]): DayRow[] => DAYS.map((day) => {
  const slot = schedule?.find((s) => s.day === day);
  return { day, on: Boolean(slot), arrival: slot?.arrival ?? '08:00', departure: slot?.departure ?? '17:30', meal: slot?.meal ?? true };
});

export const termsDefaults = (terms?: ChildcareTerms): TermsValues => ({
  from: '', net: toFieldAmount(terms?.netHourlyRate ?? ''), gross: toFieldAmount(terms?.grossHourlyRate ?? ''), overtimePct: toFieldAmount(terms?.overtimePct ?? '10'),
  upkeep: toFieldAmount(terms?.upkeepPer9h ?? ''), meal: toFieldAmount(terms?.mealPrice ?? ''), weekTypes: terms
    ? terms.weekTypes.map((w) => ({ name: w.name, weeks: String(w.weeks), days: daysOf(w.schedule) }))
    : [{ name: DEFAULT_WEEK_TYPE, weeks: '46', days: daysOf() }],
});

export const gapsDefaults = (detail?: ChildcareDetail): GapRow[] => (detail?.gaps ?? []).map((g) => ({ monday: g.monday, weeks: String(g.weeks) }));

type ErrorKey = 'date_format' | 'amount_format' | 'weeks_range' | 'name_range' | 'no_day' | 'time_format' | 'departure_before_arrival' | 'gap_weeks';
type Add = (path: (string | number)[], key: ErrorKey) => void;

const DAY_KEYS = ['childcare_form.day_1', 'childcare_form.day_2', 'childcare_form.day_3', 'childcare_form.day_4', 'childcare_form.day_5', 'childcare_form.day_6', 'childcare_form.day_7'] as const;

/** The weekday's French name, ISO day 1 = Monday. */
export const dayName = (t: TFunction, day: number): string => t(DAY_KEYS[day - 1]!);

function checkTerms(v: TermsValues, add: Add, withFrom: boolean) {
  if (withFrom && !ISO_DATE.test(v.from)) add(['from'], 'date_format');
  if (!AMOUNT.test(v.net.trim())) add(['net'], 'amount_format');
  if (v.gross.trim() && !AMOUNT.test(v.gross.trim())) add(['gross'], 'amount_format');
  if (!AMOUNT.test(v.overtimePct.trim())) add(['overtimePct'], 'amount_format');
  if (!AMOUNT.test(v.upkeep.trim())) add(['upkeep'], 'amount_format');
  if (v.meal.trim() && !AMOUNT.test(v.meal.trim())) add(['meal'], 'amount_format');
  // The sum of the weeks (46 at most) and the duplicate names are the server's to refuse.
  v.weekTypes.forEach((w, k) => {
    if (v.weekTypes.length > 1 && !(w.name.trim().length >= 1 && w.name.trim().length <= 40)) add(['weekTypes', k, 'name'], 'name_range');
    if (!(WEEKS.test(w.weeks.trim()) && Number(w.weeks) >= 1 && Number(w.weeks) <= 46)) add(['weekTypes', k, 'weeks'], 'weeks_range');
    if (!w.days.some((d) => d.on)) add(['weekTypes', k, 'days'], 'no_day');
    w.days.forEach((d, i) => {
      if (!d.on) return;
      if (!TIME.test(d.arrival)) add(['weekTypes', k, 'days', i, 'arrival'], 'time_format');
      if (!TIME.test(d.departure)) add(['weekTypes', k, 'days', i, 'departure'], 'time_format');
      else if (TIME.test(d.arrival) && d.departure <= d.arrival) add(['weekTypes', k, 'days', i, 'departure'], 'departure_before_arrival');
    });
  });
}

function checkGaps(gaps: GapRow[], add: Add) {
  gaps.forEach((g, i) => {
    if (!ISO_DATE.test(g.monday)) add(['gaps', i, 'monday'], 'date_format');
    if (!(WEEKS.test(g.weeks.trim()) && Number(g.weeks) >= 1)) add(['gaps', i, 'weeks'], 'gap_weeks');
  });
}

const termsShape = {
  from: z.string(), net: z.string(), gross: z.string(), overtimePct: z.string(), upkeep: z.string(), meal: z.string(),
  weekTypes: z.array(z.object({
    name: z.string(), weeks: z.string(),
    days: z.array(z.object({ day: z.number(), on: z.boolean(), arrival: z.string(), departure: z.string(), meal: z.boolean() })),
  })),
};
const gapShape = z.array(z.object({ monday: z.string(), weeks: z.string() }));
const adder = (ctx: z.RefinementCtx, t: TFunction): Add => (path, key) => ctx.addIssue({ code: 'custom', path, message: t(`childcare_form.error.${key}`) });

/** The creation page: every step. */
export function childcareSchema(t: TFunction) {
  return z.object({
    ...termsShape, ...ownershipShape(t),
    name: z.string().trim().min(1, t('edit.required')).max(120, t('childcare_form.error.too_long')),
    childId: z.string().min(1, t('edit.required')),
    carerName: z.string().trim().min(1, t('edit.required')).max(120, t('childcare_form.error.too_long')),
    carerPajemploi: z.string().max(40, t('childcare_form.error.too_long')), carerApproval: z.string().max(40, t('childcare_form.error.too_long')),
    gaps: gapShape,
  }).superRefine((v, ctx) => {
    checkTerms(v, adder(ctx, t), true);
    checkGaps(v.gaps, adder(ctx, t));
    checkOwnership(v, ctx, t);
  });
}

/** The edit and revision dialogs: the terms alone (a revision asks for its start, an edit keeps the current one). */
export function termsSchema(t: TFunction, withFrom: boolean) {
  return z.object(termsShape).superRefine((v, ctx) => checkTerms(v, adder(ctx, t), withFrom));
}

export type EditValues = TermsValues & Pick<ChildcareValues, 'name' | 'carerName' | 'carerPajemploi' | 'carerApproval'>;

/** The edit dialog: the name, the carer and the current terms, whose start stays. */
export function editSchema(t: TFunction) {
  return z.object({
    ...termsShape,
    name: z.string().trim().min(1, t('edit.required')).max(120, t('childcare_form.error.too_long')),
    carerName: z.string().trim().min(1, t('edit.required')).max(120, t('childcare_form.error.too_long')),
    carerPajemploi: z.string().max(40, t('childcare_form.error.too_long')), carerApproval: z.string().max(40, t('childcare_form.error.too_long')),
  }).superRefine((v, ctx) => checkTerms(v, adder(ctx, t), false));
}

export function gapsSchema(t: TFunction) {
  return z.object({ gaps: gapShape }).superRefine((v, ctx) => checkGaps(v.gaps, adder(ctx, t)));
}

export function termsToWire(v: TermsValues, from: string): TermsBody {
  return {
    from, netHourlyRate: toWireAmount(v.net), ...(v.gross.trim() ? { grossHourlyRate: toWireAmount(v.gross) } : {}),
    overtimePct: toWireAmount(v.overtimePct), upkeepPer9h: toWireAmount(v.upkeep), ...(v.meal.trim() ? { mealPrice: toWireAmount(v.meal) } : {}),
    weekTypes: v.weekTypes.map((w) => ({
      name: w.name.trim() || DEFAULT_WEEK_TYPE, weeks: Number(w.weeks),
      schedule: w.days.filter((d) => d.on).map((d) => ({ day: d.day, arrival: d.arrival, departure: d.departure, meal: d.meal })),
    })),
  };
}

export const gapsToWire = (gaps: GapRow[]) => gaps.map((g) => ({ monday: g.monday, weeks: Number(g.weeks) }));

export function carerToWire(v: Pick<ChildcareValues, 'carerName' | 'carerPajemploi' | 'carerApproval'>) {
  return { name: v.carerName.trim(), ...(v.carerPajemploi.trim() ? { pajemploi: v.carerPajemploi.trim() } : {}), ...(v.carerApproval.trim() ? { approval: v.carerApproval.trim() } : {}) };
}

export const toCreateBody = (v: ChildcareValues): CreateChildcareBody => ({
  name: v.name.trim(), childId: v.childId, carer: carerToWire(v), terms: termsToWire(v, v.from), gaps: gapsToWire(v.gaps), ...ownershipToWire(v),
});

/**
 * A server path to the form field that owns it. The wire's schedule lists the checked days only: `schedule[1]` of a week type
 * is the second checked row of its `days`. An exception pointing at a dropped rank (`weekOverrides[…]`) is the week types' as a whole.
 */
export function childcarePath(path: string, weekTypes: WeekTypeRow[]): Path<ChildcareValues> | undefined {
  const top: Record<string, Path<ChildcareValues>> = {
    name: 'name', childId: 'childId', 'carer.name': 'carerName', 'carer.pajemploi': 'carerPajemploi', 'carer.approval': 'carerApproval',
    'terms.from': 'from', 'terms.netHourlyRate': 'net', 'terms.grossHourlyRate': 'gross', 'terms.overtimePct': 'overtimePct',
    'terms.upkeepPer9h': 'upkeep', 'terms.mealPrice': 'meal', 'terms.weekTypes': 'weekTypes', weekTypes: 'weekTypes', gaps: 'gaps',
  };
  if (Object.hasOwn(top, path)) return top[path];
  if (path.startsWith('weekOverrides')) return 'weekTypes';
  const type = /^(?:terms\.)?weekTypes\[(\d+)\]\.(.+)$/.exec(path);
  if (type) {
    const k = Number(type[1]);
    const field = type[2]!;
    if (field === 'name' || field === 'weeks') return `weekTypes.${k}.${field}`;
    const slot = /^schedule\[(\d+)\](?:\.(day|arrival|departure))?$/.exec(field);
    if (slot) {
      const row = (weekTypes[k]?.days ?? []).map((d, i) => (d.on ? i : -1)).filter((i) => i >= 0)[Number(slot[1])];
      return row === undefined ? `weekTypes.${k}.days` : `weekTypes.${k}.days.${row}.${slot[2] === 'day' || !slot[2] ? 'on' : (slot[2] as 'arrival' | 'departure')}`;
    }
    return field === 'schedule' ? `weekTypes.${k}.days` : undefined;
  }
  const gap = /^gaps\[(\d+)\]\.(monday|weeks)$/.exec(path);
  if (gap) return `gaps.${Number(gap[1])}.${gap[2] as 'monday' | 'weeks'}`;
  return ownershipPath(path);
}
