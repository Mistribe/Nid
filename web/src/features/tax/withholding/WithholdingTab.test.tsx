import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { estimate } from '../taxTestData';

const open = async (loaded = estimate()) => {
  server.use(http.get('*/api/v1/tax', () => HttpResponse.json(loaded)));
  const view = await renderApp('/tax?tab=withholding');
  await screen.findByRole('heading', { name: 'Est-ce que je suis assez (ou trop) prélevé ?' });
  return view;
};
const cutOf = () => {
  const loaded = estimate();
  loaded.result.withholding.cut = {
    rate: '4.2', declarants: [{ name: 'Alice', salaries: '37518.00', monthly: '141.49', pensions: '0.00' }],
    rentalMonthly: '0.00', annual: '3005.70', less: '214.69', balance: '-2758.70',
  };
  return loaded;
};

describe('WithholdingTab', () => {
  beforeAll(async () => { await import('../TaxPage'); }, 30_000);

  it('opens on its question and a line of figures', async () => {
    await open();
    const line = screen.getByRole('tabpanel').querySelector('[data-stat-line]')!;
    const text = line.textContent.replace(/\s/g, ' ');
    expect(text).toContain('Taux du foyer4,5 %');
    expect(text).toContain('Alice : prélevé chaque mois151,59 €');
  });

  it('draws withheld against due, the gap in words', async () => {
    await open();
    const card = screen.getByRole('region', { name: 'Ce que le taux devrait donner' });
    await waitFor(() => expect(card.querySelectorAll('.recharts-bar-rectangle')).toHaveLength(2));
    const labels = () => [...card.querySelectorAll('.recharts-label-list text')].map((n) => n.textContent?.replace(/\s/g, ' '));
    await waitFor(() => expect(labels()).toEqual(['3 220,39 €', '247,00 €']));
    expect(within(card).getByText(/Remboursé.*: 2 973,39 €/)).toBeInTheDocument();
    expect(screen.queryByText(/-2 973,39/)).toBeNull();
  });

  it('a balance due reads to pay', async () => {
    const loaded = estimate();
    loaded.result.withholding.balance = '120.00';
    await open(loaded);
    expect(screen.getByText(/À payer.*: 120,00 €/)).toBeInTheDocument();
  });

  it('a zero balance reads nothing to settle', async () => {
    const loaded = estimate();
    loaded.result.withholding.balance = '0.00';
    await open(loaded);
    const words = screen.getByText('Rien à régler');
    expect(words.textContent).not.toContain('0,00 €');
  });

  it('the table shows the same two figures', async () => {
    const { user } = await open();
    await user.click(screen.getByRole('button', { name: 'Afficher en tableau' }));
    const table = screen.getByRole('table', { name: 'Prélevé sur l’année et impôt dû' });
    expect(within(table).getAllByRole('row')).toHaveLength(2);
    expect(table).toHaveTextContent('3 220,39 €');
    expect(table).toHaveTextContent('247,00 €');
  });

  it('lists what to type without a header row', async () => {
    await open();
    const card = screen.getByRole('region', { name: 'À saisir sur impots.gouv.fr' });
    expect(within(card).queryByRole('columnheader')).toBeNull();
    expect(screen.getByText('Alice : salaires nets imposables de l’année', { selector: 'dt' }).nextElementSibling).toHaveTextContent('40 424,63 €');
  });

  it('offers the safe cut with its balance in words', async () => {
    const { user } = await open(cutOf());
    const card = screen.getByRole('region', { name: 'Baisse sans risque' });
    expect(within(card).getByText('Prélevé en moins sur l’année')).toBeInTheDocument();
    expect(within(card).getByText('Solde restant à la déclaration')).toBeInTheDocument();
    expect(within(card).getByText('Alice : salaires à saisir', { selector: 'dt' }).nextElementSibling).toHaveTextContent('37 518,00 €');
    expect(screen.queryByText(/négatif/)).toBeNull();
    expect(screen.queryByText(/tolérance de 10 %/)).toBeNull();
    await user.click(within(card).getByRole('button', { name: 'En savoir plus' }));
    expect(screen.getByText(/tolérance de 10 %/)).toBeInTheDocument();
  });

  it('no safe cut card without a cut', async () => {
    await open();
    expect(screen.queryByRole('region', { name: 'Baisse sans risque' })).toBeNull();
  });

  it('has no Section and no axe violation', async () => {
    const { container } = await open(cutOf());
    expect(screen.getByRole('tabpanel').querySelectorAll('section')).toHaveLength(0);
    await expectNoAxeViolations(container);
  });
});
