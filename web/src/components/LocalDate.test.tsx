import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { formatLocalDate, LocalDate } from './LocalDate';

const originalTz = process.env.TZ;
afterEach(() => {
  // Assigning undefined would set the string "undefined": an unset TZ is restored by deleting it.
  if (originalTz === undefined) delete process.env.TZ;
  else process.env.TZ = originalTz;
});

describe('formatLocalDate', () => {
  it('writes the day and month in words', () => expect(formatLocalDate('2026-09-19')).toBe('19 septembre 2026'));
  it('abbreviates on request', () => expect(formatLocalDate('2026-09-19', 'short')).toBe('19 sept. 2026'));
  it('handles the last day of the year', () => expect(formatLocalDate('2026-12-31')).toBe('31 décembre 2026'));
  it('handles a leap day', () => expect(formatLocalDate('2024-02-29')).toBe('29 février 2024'));

  // new Date("2026-01-01") is UTC midnight: shown in a zone behind UTC it becomes 31 December.
  it.each(['Pacific/Pago_Pago', 'Pacific/Kiritimati', 'Europe/Paris'])('gives the same day in %s', (tz) => {
    process.env.TZ = tz;
    expect(formatLocalDate('2026-01-01')).toBe('1 janvier 2026');
  });

  it.each(['2025-02-29', '2026-13-01', '19/09/2026', '2026-9-1'])('refuses %j', (value) =>
    expect(() => formatLocalDate(value)).toThrow(RangeError));
});

describe('<LocalDate>', () => {
  it('renders a machine-readable <time>', () => {
    render(<LocalDate value="2026-09-19" />);
    expect(screen.getByText('19 septembre 2026')).toHaveAttribute('datetime', '2026-09-19');
  });
});
