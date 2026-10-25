const MONTHS: Record<string, number> = {
  janvier: 1, janv: 1, jan: 1,
  fevrier: 2, fevr: 2, fev: 2,
  mars: 3, mar: 3,
  avril: 4, avr: 4,
  mai: 5,
  juin: 6,
  juillet: 7, juil: 7,
  aout: 8,
  septembre: 9, sept: 9, sep: 9,
  octobre: 10, oct: 10,
  novembre: 11, nov: 11,
  decembre: 12, dec: 12,
};

const ISO = /^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})(?:[t ].*)?$/;
const DAY_FIRST = /^(\d{1,2})[-/.](\d{1,2})[-/.](\d{2}|\d{4})$/;
const IN_WORDS = /^(\d{1,2})(?:er)? ([a-z]+)\.? (\d{4})$/;

function iso(year: number, month: number, day: number): string | null {
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null;
  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

/**
 * A pasted date as yyyy-mm-dd, or null. French order only: 03/04/2026 is 3 April, never 4 March.
 * A two-digit year is in the 2000s.
 */
export function parsePastedDate(text: string): string | null {
  const clean = text
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/\s+/g, ' ')
    .trim();
  let match = ISO.exec(clean);
  if (match) return iso(Number(match[1]), Number(match[2]), Number(match[3]));
  match = DAY_FIRST.exec(clean);
  if (match) {
    const year = match[3]!.length === 2 ? 2000 + Number(match[3]) : Number(match[3]);
    return iso(year, Number(match[2]), Number(match[1]));
  }
  match = IN_WORDS.exec(clean);
  if (match) {
    const month = MONTHS[match[2]!];
    return month ? iso(Number(match[3]), month, Number(match[1])) : null;
  }
  return null;
}
