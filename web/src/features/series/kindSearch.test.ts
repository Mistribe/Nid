import { describe, expect, it } from 'vitest';
import { kindsMatching } from './kindSearch';

describe('kindsMatching', () => {
  it('finds a kind by the French words of its label', () => {
    expect(kindsMatching('ipc')).toEqual(['cpi']);
    expect(kindsMatching('Loyer')).toEqual(['irl', 'tenancy_rent']);
    expect(kindsMatching('revenu')).toEqual(['income', 'income_household']);
    expect(kindsMatching('inflation')).toEqual(['cpi_yoy', 'projection_inflation']);
    expect(kindsMatching('projete')).toEqual(['projection_inflation', 'projection_rate', 'networth_projected']);
    expect(kindsMatching('obligations')).toEqual(['projection_rate']);
    expect(kindsMatching('patrimoine')).toEqual(['networth', 'networth_projected']);
    expect(kindsMatching('impot')).toEqual(['tax_yearly']);
    expect(kindsMatching('abonnement')).toEqual(['subscriptions_yearly']);
    expect(kindsMatching('garde')).toEqual(['childcare_monthly']);
  });
  it('finds nothing for unknown or empty text', () => {
    expect(kindsMatching('zzz')).toEqual([]);
    expect(kindsMatching('  ')).toEqual([]);
  });
});
