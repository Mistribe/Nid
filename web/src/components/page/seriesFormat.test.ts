import { describe, expect, it } from 'vitest';
import i18n from '@/app/i18n';
import { seriesLabel } from './seriesFormat';

describe('seriesLabel', () => {
  it('names an item series by its kind and name', () => {
    expect(seriesLabel(i18n.t, { kind: 'loan_remaining', name: 'Prêt maison', key: 'loan:x:remaining' })).toBe('Capital restant dû : Prêt maison');
    expect(seriesLabel(i18n.t, { kind: 'income_household', name: null, key: 'income:household' })).toBe('Revenus du foyer');
    expect(seriesLabel(i18n.t, { kind: 'goal', name: 'Vacances', key: 'goal:g1' })).toBe('Objectif : Vacances');
    expect(seriesLabel(i18n.t, { kind: 'networth_projected', name: null, key: 'networth:projected' })).toBe('Patrimoine projeté');
  });
});
