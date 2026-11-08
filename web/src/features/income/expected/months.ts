import type { IncomeExpected } from '../api';

/** A calendar month as numbers: arithmetic on a year and a month index, never on an amount. */
type Month = { year: number; month: number };

const pad = (n: number, width = 2) => String(n).padStart(width, '0');
const monthOf = (iso: string): Month => ({ year: Number(iso.slice(0, 4)), month: Number(iso.slice(5, 7)) });
const shift = ({ year, month }: Month, delta: number): Month => {
  const index = year * 12 + (month - 1) + delta;
  return { year: Math.floor(index / 12), month: (index % 12) + 1 };
};
const firstDay = ({ year, month }: Month) => `${pad(year, 4)}-${pad(month)}-01`;
// Day 0 of the next month is this month's last day.
const lastDay = ({ year, month }: Month) => `${pad(year, 4)}-${pad(month)}-${pad(new Date(Date.UTC(year, month, 0)).getUTCDate())}`;

export const EXPECTED_PRESETS = ['last12', 'next12', 'year', 'next_year'] as const;
export type ExpectedPreset = (typeof EXPECTED_PRESETS)[number];

/** Spec §5: the current month and the eleven before or after; this calendar year; next calendar year. */
export function expectedRange(preset: ExpectedPreset, today: string): { from: string; to: string } {
  const now = monthOf(today);
  switch (preset) {
    case 'last12': return { from: firstDay(shift(now, -11)), to: lastDay(now) };
    case 'next12': return { from: firstDay(now), to: lastDay(shift(now, 11)) };
    case 'year': return { from: firstDay({ year: now.year, month: 1 }), to: lastDay({ year: now.year, month: 12 }) };
    case 'next_year': return { from: firstDay({ year: now.year + 1, month: 1 }), to: lastDay({ year: now.year + 1, month: 12 }) };
  }
}

/** Spec §3: the twelve months before this one, this one and the eleven after — under the server's two-year bound. */
export function paymentWindow(today: string): { from: string; to: string } {
  const now = monthOf(today);
  return { from: firstDay(shift(now, -12)), to: lastDay(shift(now, 11)) };
}

/**
 * Spec §6: from the oldest phase's start to the end of this month, as consecutive windows of 24 months at most: each ends
 * before its start plus two years, the server's bound on one expected-income call.
 */
export function sinceWindows(start: string, today: string): { from: string; to: string }[] {
  const index = ({ year, month }: Month) => year * 12 + month;
  const last = monthOf(today);
  const windows: { from: string; to: string }[] = [];
  for (let m = monthOf(start), from = start; index(m) <= index(last); m = shift(m, 24), from = firstDay(m)) {
    const end = shift(m, 23);
    windows.push({ from, to: lastDay(index(end) < index(last) ? end : last) });
  }
  return windows;
}

/** Spec §3: a phase's payment dates in an expected-income answer, ascending, without its own start (a change cannot fall on it). */
export function paymentDates(expected: IncomeExpected, phase: { id: string; startsOn: string }): string[] {
  const dates = expected.months.flatMap((m) => m.occurrences)
    .filter((o) => o.phaseId === phase.id && o.on !== phase.startsOn)
    .map((o) => o.on);
  return [...new Set(dates)].sort();
}

/** The first date on or after today, the last one otherwise; nothing when there is no date. */
export function defaultEffectiveDate(dates: string[], today: string): string | undefined {
  return dates.find((d) => d >= today) ?? dates[dates.length - 1];
}
