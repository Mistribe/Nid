import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import '@/locales/tax';
import type { TaxEstimate } from '@/api/tax';
import { estimate, history, previous } from '../taxTestData';
import { TaxHero } from './TaxHero';

const show = (over: Partial<TaxEstimate> = {}, onTab = vi.fn()) => {
  render(<TaxHero estimate={{ ...estimate(), previous: previous(), history: history(), ...over }} onTab={onTab} />);
  return screen.getByRole('region', { name: 'Impôt total 2026 · estimation' });
};

describe('TaxHero', () => {
  it('leads with the year’s total tax, said to be an estimate', () => {
    const region = show();
    expect(region).toHaveTextContent('247,00 €');
    expect(region).toHaveTextContent('Taux moyen 0,38 % · Estimation selon le barème 2025');
  });

  it('measures the change on income tax against last year’s notice, a rise in the critical tone', () => {
    const region = show();
    expect(region).toHaveTextContent('Impôt sur le revenu+360,00 €+5,1 %en haussepar rapport à l’avis 2025');
    expect(region.querySelector('svg.text-status-critical')).not.toBeNull();
  });

  it('a fall against last year’s estimate reads in the good tone', () => {
    const region = show({ previous: previous({ source: 'estimate', change: '-200.00', changePct: '-2.7' }) });
    expect(region.querySelector('svg.text-status-good')).not.toBeNull();
    expect(region).toHaveTextContent('par rapport à l’estimation 2025');
  });

  it('no change line without a previous year, no curve with one year', () => {
    const region = show({ previous: null, history: history().slice(0, 1) });
    expect(within(region).queryByText(/par rapport/)).toBeNull();
    expect(region.querySelector('.w-56')).toBeNull();
  });

  it('draws the curve from the server’s history', () => {
    expect(show().querySelector('.w-56 svg')).not.toBeNull();
  });

  it('six figures in three columns, each with its tip', () => {
    const region = show();
    expect(within(region).getAllByRole('term').map((d) => d.textContent)).toEqual(
      ['Reste par mois', 'Remboursé en été 2027', 'Revenu imposable', 'Votre tranche d’imposition', 'Taux de prélèvement', 'Parts du foyer']);
    const defs = within(region).getAllByRole('definition').map((d) => d.textContent?.replace(/\s/g, ' '));
    for (const v of ['5 729,42 €', '2 973,39 €', '64 407,00 €', '11 %', '4,5 %', '3']) expect(defs).toContain(v);
    expect(region.querySelector('[data-stat-line]')).toHaveClass('md:grid-cols-3');
    expect(within(region).getAllByRole('button', { name: /^À propos : / })).toHaveLength(6);
    expect(region).not.toHaveTextContent('-2 973');
  });

  it('the adjust link opens Prélèvement', async () => {
    const onTab = vi.fn();
    const region = show({}, onTab);
    await userEvent.click(within(region).getByRole('button', { name: 'Ajuster le prélèvement' }));
    expect(onTab).toHaveBeenCalledWith('withholding');
  });
});
