export type Preset = 'ytd' | 'last12' | 'previous_year' | 'custom';

const isLeap = (y: number) => (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;

/** The same calendar day in another year; 29 February becomes 28 February when that year has none. */
const withYear = (iso: string, year: number): string => {
  const [, m, d] = iso.split('-');
  const day = m === '02' && d === '29' && !isLeap(year) ? '28' : d;
  return `${String(year).padStart(4, '0')}-${m}-${day}`;
};

/** The period of a preset for a given today (ISO), as ISO dates; 29 February minus a year is 28 February. */
export function presetRange(preset: Exclude<Preset, 'custom'>, today: string): { from: string; to: string } {
  const year = Number(today.slice(0, 4)); // a calendar year, not an amount
  switch (preset) {
    case 'ytd': return { from: `${today.slice(0, 4)}-01-01`, to: today };
    case 'last12': return { from: withYear(today, year - 1), to: today };
    case 'previous_year': return { from: withYear('2000-01-01', year - 1), to: withYear('2000-12-31', year - 1) };
  }
}
