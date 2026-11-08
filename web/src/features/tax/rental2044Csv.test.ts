import { beforeAll, describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import type { Rental2044 } from '@/api/tax';
import { rental2044Csv } from './rental2044Csv';

const view = (over: Partial<Rental2044> = {}): Rental2044 => ({
  year: 2025, regime: 'real', provisional: false,
  properties: [
    { id: 'a', name: 'Studio', lines: [{ line: '211', amount: '6000.00' }, { line: '263', amount: '4500.00' }] },
    { id: 'b', name: 'Loft; "Nord"', lines: [{ line: '211', amount: '1200.00' }] },
  ],
  totals: {
    receipts: '7200.00', charges: '1500.00', interest: '0.00', result: '5700.00', interestDeficit: '0.00', chargesDeficit: '0.00',
    globalDeficit: '0.00', carriedDeficit: '0.00', box4BA: '5700.00', box4BB: '0.00', box4BC: '0.00', box4BE: '0.00',
  },
  toCheck: [], notices: [],
  ...over,
});

describe('rental2044Csv', () => {
  beforeAll(async () => { await import('@/locales/tax'); });

  it('writes one row per property line, then the household totals, as the server sent them', () => {
    expect(rental2044Csv(view(), i18n.t)).toBe([
      '﻿Bien;Ligne;Libellé;Montant',
      'Studio;211;Loyers bruts;6000,00',
      'Studio;263;Revenus nets du bien;4500,00',
      '"Loft; ""Nord""";211;Loyers bruts;1200,00',
      'Foyer;420;Résultat;5700,00',
      'Foyer;440;Déficit imputable sur le revenu global (case 4BC);0,00',
      'Foyer;441;Déficit reportable (case 4BB);0,00',
      'Foyer;4BA;Revenu foncier imposable;5700,00',
      '',
    ].join('\r\n'));
  });

  it('writes only the 4BE case under the micro-foncier', () => {
    const csv = rental2044Csv(view({ regime: 'micro', totals: { ...view().totals, box4BE: '7200.00' } }), i18n.t);
    expect(csv).toBe('﻿Bien;Ligne;Libellé;Montant\r\nFoyer;4BE;Recettes brutes (case 4BE);7200,00\r\n');
  });

  it('numbers the deficit lines 435 and 438 when the interest exceeds the rents', () => {
    const csv = rental2044Csv(view({ totals: { ...view().totals, interestDeficit: '300.00' } }), i18n.t);
    expect(csv).toContain('Foyer;435;');
    expect(csv).toContain('Foyer;438;');
    expect(csv).not.toContain('Foyer;440;');
  });
});
