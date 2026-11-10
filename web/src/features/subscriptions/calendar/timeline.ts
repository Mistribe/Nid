// The timeline's months (issue #65), as `yyyy-mm` strings: they sort as text, so min / max are string comparisons.
import { daysIn, shiftMonth } from './monthGrid';

/** The band ends this many months after the current one. */
export const AHEAD = 12;
const STEP = 3;

export type Range = { from: string; to: string };

const later = (a: string, b: string) => (a > b ? a : b);
const earlier = (a: string, b: string) => (a < b ? a : b);
/** The month of the first charge, never after the current one; none known: the current month. */
const floor = (current: string, first: string | null) => (first === null ? current : earlier(first, current));

export function months(range: Range): string[] {
  const out: string[] = [];
  for (let m = range.from; m <= range.to; m = shiftMonth(m, 1)) out.push(m);
  return out;
}

export function initialRange(current: string, first: string | null): Range {
  return { from: later(shiftMonth(current, -2), floor(current, first)), to: shiftMonth(current, 3) };
}

/** A few more months on one side, within the bounds; the same range when already there. */
export function extend(range: Range, side: 'past' | 'future', current: string, first: string | null): Range {
  if (side === 'past') {
    const from = later(shiftMonth(range.from, -STEP), floor(current, first));
    return from < range.from ? { ...range, from } : range;
  }
  const to = earlier(shiftMonth(range.to, STEP), shiftMonth(current, AHEAD));
  return to > range.to ? { ...range, to } : range;
}

export const atStart = (range: Range, current: string, first: string | null) => range.from <= floor(current, first);
export const atEnd = (range: Range, current: string) => range.to >= shiftMonth(current, AHEAD);

/** Where a day sits in its month: 0 on the 1st, 1 on the last day. */
export function dayOffset(date: string): number {
  return (Number(date.slice(8, 10)) - 1) / (daysIn(Number(date.slice(0, 4)), Number(date.slice(5, 7))) - 1);
}

/** One lane per subscription met in the loaded months, by name: the first charge seen names it. */
export function lanes<L extends { itemId: string; displayName: string }>(answers: { charges: L[] }[]): L[] {
  const byItem = new Map<string, L>();
  for (const a of answers) for (const c of a.charges) if (!byItem.has(c.itemId)) byItem.set(c.itemId, c);
  return [...byItem.values()].sort((a, b) => a.displayName.localeCompare(b.displayName, 'fr'));
}
