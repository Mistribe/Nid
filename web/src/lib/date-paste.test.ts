import { describe, expect, it } from 'vitest';
import { parsePastedDate } from './date-paste';

describe('parsePastedDate', () => {
  it.each([
    ['2026-10-18', '2026-10-18'],
    ['2026-10-18T09:30:00Z', '2026-10-18'],
    ['2026/10/18', '2026-10-18'],
    ['18/10/2026', '2026-10-18'],
    ['3/4/2026', '2026-04-03'],
    ['18.10.2026', '2026-10-18'],
    ['18-10-2026', '2026-10-18'],
    ['18/10/26', '2026-10-18'],
    ['  18/10/2026\n', '2026-10-18'],
    ['18 octobre 2026', '2026-10-18'],
    ['1er août 2026', '2026-08-01'],
    ['3 févr. 2026', '2026-02-03'],
    ['18 Oct 2026', '2026-10-18'],
    ['29/02/2024', '2024-02-29'],
  ])('reads %j', (text, expected) => expect(parsePastedDate(text)).toBe(expected));

  it.each(['', 'hier', '29/02/2025', '31/04/2026', '13/13/2026', '18 brumaire 2026', '1234', '2026-10'])('refuses %j', (text) =>
    expect(parsePastedDate(text)).toBeNull());
});
