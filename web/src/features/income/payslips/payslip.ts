import type { TFunction } from 'i18next';
import type { DocumentLink, DocumentSummary } from '../../documents/api';
import '@/locales/income';

/** `'YYYY-MM'`: compared as a string, never computed with. */
export type MonthKey = string;

const pad = (n: number) => String(n).padStart(2, '0');

export const monthKey = (isoDate: string): MonthKey => isoDate.slice(0, 7);
export const monthKeyOf = (year: number, month: number): MonthKey => `${year}-${pad(month)}`;

const MONTH_NAMES: [string, number][] = [
  ['janvier|janv|jan', 1], ['fevrier|fevr|fev', 2], ['mars|mar', 3], ['avril|avr', 4], ['mai', 5], ['juin', 6],
  ['juillet|juil', 7], ['aout', 8], ['septembre|sept|sep', 9], ['octobre|oct', 10], ['novembre|nov', 11], ['decembre|dec', 12],
];
const MONTH = '(0[1-9]|1[0-2])';
const YEAR = '(20\\d{2})';
const NUMERIC_FORMS = [
  { re: new RegExp(`(?<!\\d)${YEAR}[-_.]?${MONTH}(?!\\d)`), year: 1, month: 2 },
  { re: new RegExp(`(?<!\\d)${MONTH}[-_.]?${YEAR}(?!\\d)`), year: 2, month: 1 },
];
const NAMED_FORMS = MONTH_NAMES.map(([names, month]) => ({ re: new RegExp(`(?<![a-z])(?:${names})[\\s_.-]*${YEAR}(?!\\d)`), month }));

/** The month a file name designates (`2026-09`, `092026`, `septembre 2026`…), else null. */
export function monthOfFileName(name: string): MonthKey | null {
  const text = name.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLowerCase();
  for (const { re, year, month } of NUMERIC_FORMS) {
    const m = re.exec(text);
    if (m) return `${m[year]}-${m[month]}`;
  }
  for (const { re, month } of NAMED_FORMS) {
    const m = re.exec(text);
    if (m) return monthKeyOf(Number(m[1]), month);
  }
  return null;
}

const onSource = (l: DocumentLink, sourceId: string, phaseIds: ReadonlySet<string>) =>
  (l.targetKind === 'core.item' && l.targetId === sourceId) || (l.targetKind === 'income.phase' && phaseIds.has(l.targetId));

export const isPayslipOfSource = (doc: DocumentSummary, sourceId: string, phaseIds: ReadonlySet<string>): boolean =>
  doc.links.some((l) => l.role === 'payslip' && onSource(l, sourceId, phaseIds));

/** The links « Retirer » removes, whatever their role; links to other items or the household stay. */
export const linksOnSource = (doc: DocumentSummary, sourceId: string, phaseIds: ReadonlySet<string>): DocumentLink[] =>
  doc.links.filter((l) => onSource(l, sourceId, phaseIds));

/** Payslips of the source by the month of their document date (the payment date plays no part). */
export function coverage(docs: DocumentSummary[], sourceId: string, phaseIds: ReadonlySet<string>) {
  const byMonth = new Map<MonthKey, DocumentSummary[]>();
  const undated: DocumentSummary[] = [];
  let oldest: MonthKey | null = null;
  for (const doc of docs) {
    if (!isPayslipOfSource(doc, sourceId, phaseIds)) continue;
    if (!doc.documentDate) { undated.push(doc); continue; }
    const key = monthKey(doc.documentDate);
    byMonth.set(key, [...(byMonth.get(key) ?? []), doc]);
    if (oldest === null || key < oldest) oldest = key;
  }
  return { byMonth, undated, oldest };
}

export const isMissing = (month: MonthKey, a: { salary: boolean; pastPayment: boolean; oldest: MonthKey | null; covered: boolean }): boolean =>
  a.salary && a.pastPayment && !a.covered && a.oldest !== null && month >= a.oldest;

/** The month's earliest payment (first on a tie), its date capped to today; null when the month is still to come. */
export function attachTarget(occurrences: { phaseId: string; on: string }[], today: string): { phaseId: string; date: string } | null {
  const first = occurrences.reduce<{ phaseId: string; on: string } | null>((best, o) => (best === null || o.on < best.on ? o : best), null);
  if (first === null || monthKey(first.on) > monthKey(today)) return null;
  return { phaseId: first.phaseId, date: first.on < today ? first.on : today };
}

export const oneOffDocuments = (docs: DocumentSummary[], oneOffId: string): DocumentSummary[] =>
  docs.filter((d) => d.links.some((l) => l.targetKind === 'income.one_off' && l.targetId === oneOffId));

export function payslipTitle(t: TFunction, month: MonthKey): string {
  const [year, m] = month.split('-').map(Number) as [number, number];
  const name = new Intl.DateTimeFormat('fr-FR', { month: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(year, m - 1, 1)));
  return t('income_detail.payslips.title', { month: `${name} ${year}` });
}

export const oneOffTitle = (t: TFunction, oneOff: { note?: string | null; occurredOn: string }): string =>
  oneOff.note || t('income_detail.payslips.one_off_title', { date: oneOff.occurredOn.split('-').reverse().join('/') });
