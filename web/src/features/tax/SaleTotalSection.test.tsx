import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { PROPERTY } from '../properties/testData';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { SaleItemView, SaleTax } from './api';
import { estimate } from './taxTestData';

const ENVELOPE = '0192f0aa-0000-7000-8000-0000000000e1';
const item = (over: Partial<SaleItemView> = {}): SaleItemView => ({
  item: PROPERTY, module: 'properties', name: 'Maison', kind: 'secondary_residence', status: 'taxed',
  value: '290000.00', valueOn: '2026-06-01', estimated: false, stale: false, approximate: false, yearsHeld: 6,
  gain: '9000.00', incomeTax: '1710.00', socialTax: '1548.00', surtax: '0.00', net: '286742.00', notes: [], ...over,
});
const pea = item({
  item: ENVELOPE, module: 'investments', name: 'PEA', kind: 'pea', value: '50000.00', gain: '5000.00', incomeTax: '0.00',
  socialTax: '860.00', net: '49140.00', envelope: { remainingCapital: '45000.00' },
});
const view = (over: Partial<SaleTax> = {}): SaleTax => ({
  currency: 'EUR', rulesYear: 2026, carriedForward: false, total: true, items: [item(), pea],
  totals: { value: '340000.00', incomeTax: '1710.00', socialTax: '2408.00', surtax: '0.00', net: '335882.00' },
  notes: ['total_not_sum_of_pages'], ...over,
});
const seen: (string | null)[] = [];
const serve = (v: SaleTax) => server.use(
  http.get('*/api/v1/tax', () => HttpResponse.json(estimate())),
  http.get('*/api/v1/tax/sale', ({ request }) => {
    seen.push(new URL(request.url).searchParams.get('scope'));
    return HttpResponse.json(v);
  }),
);

describe('SaleTotalSection', () => {
  beforeAll(async () => { await import('./TaxPage'); }, 30_000);

  it('shows the totals, the note and one row per item with its detail in a dialog', async () => {
    serve(view());
    const { container, user } = await renderApp('/tax?tab=simulation');

    expect(await screen.findByText('340 000,00 €')).toBeInTheDocument();
    expect(screen.getByText('335 882,00 €')).toBeInTheDocument();
    expect(screen.getByText(/^Estimation de l’impôt dû si chaque bien et chaque placement étaient vendus aujourd’hui\./)).toBeInTheDocument();
    expect(screen.getByText(/Le total n’est pas la somme des fiches/)).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Impôt par bien et par enveloppe' });
    const rows = within(table).getAllByRole('row');
    expect(within(rows[1]!).getByRole('link', { name: 'Maison' })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(rows[1]).toHaveTextContent('1 710,00 €');
    expect(rows[1]).toHaveTextContent('286 742,00 €');
    expect(within(rows[2]!).getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${ENVELOPE}`);
    await expectNoAxeViolations(container);
    expect(seen.at(-1)).toBe('household');

    await user.click(within(rows[2]!).getByRole('button', { name: 'Détail : PEA' }));
    const dialog = await screen.findByRole('dialog', { name: 'PEA' });
    expect(within(dialog).getByText('Capital restant', { selector: 'dt' }).nextElementSibling).toHaveTextContent('45 000,00 €');
    await expectNoAxeViolations(dialog);
  });

  it('says the previous year’s rates are used, and the scope follows the page', async () => {
    serve(view({ carriedForward: true }));
    await renderApp('/tax?tab=simulation&scope=me');
    expect(await screen.findByText(/Taux de l’année précédente repris/)).toBeInTheDocument();
    expect(seen.at(-1)).toBe('me');
  });

  it('says « approximatif », « valeur estimée » and « valeur à mettre à jour » on the row of an estimated item', async () => {
    serve(view({ items: [item({ approximate: true, estimated: true, stale: true })] }));
    await renderApp('/tax?tab=simulation');

    const row = within(await screen.findByRole('table', { name: 'Impôt par bien et par enveloppe' })).getAllByRole('row')[1]!;
    expect(row).toHaveTextContent('Imposable · approximatif · valeur estimée · valeur à mettre à jour');
  });

  it('sits under the simulation in « Simuler », the year picker and the notes above unchanged', async () => {
    serve(view());
    await renderApp('/tax?tab=simulation');
    await screen.findByText('340 000,00 €');
    expect(screen.getByRole('heading', { name: 'Simulation' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Si tout était vendu' })).toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: 'Année des revenus' })).toBeInTheDocument();
    expect(screen.getByText('Estimation selon le barème 2025')).toBeInTheDocument();
  });

  it('shows an empty state without item', async () => {
    serve(view({ items: [], totals: null, notes: [] }));
    await renderApp('/tax?tab=simulation');
    expect(await screen.findByRole('heading', { name: 'Rien à vendre' })).toBeInTheDocument();
  });
});
