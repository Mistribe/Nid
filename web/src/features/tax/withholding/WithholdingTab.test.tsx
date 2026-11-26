import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { reduceMotion } from '@/test/motion';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import i18n from 'i18next';
import { estimate } from '../taxTestData';
import { withheldBars } from './WithholdingTab';

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
    rentalMonthly: '0.00', annual: '3005.70', less: '214.69', balance: '-2758.70', months: 12,
  };
  return loaded;
};

describe('WithholdingTab', () => {
  reduceMotion();
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
    await waitFor(() => expect(labels()).toEqual(['Prélevé sur l’année : 3 220,39 €', 'Impôt dû : 247,00 €']));
    expect(within(card).getByText(/Remboursé.*: 2 973,39 €/)).toBeInTheDocument();
    expect(screen.queryByText(/-2 973,39/)).toBeNull();
  });

  it('a balance due reads to pay', async () => {
    const loaded = estimate();
    loaded.result.withholding.balance = '120.00';
    await open(loaded);
    expect(screen.getByText(/À payer.*: 120,00 €/)).toBeInTheDocument();
  });

  it('a refund draws an empty due bar and says it in words', async () => {
    const loaded = estimate();
    loaded.result.incomeTax = '-300.00';
    await open(loaded);
    const card = screen.getByRole('region', { name: 'Ce que le taux devrait donner' });
    await waitFor(() => expect(card.textContent?.replace(/\s/g, ' ')).toContain('300,00 € en votre faveur'));
    expect(card.textContent).not.toContain('-300');
    const due = withheldBars(i18n.t.bind(i18n), loaded)[1]!;
    expect(due.value).toBe(0.01);
    expect(due.text.replace(/\s/g, ' ')).toBe('300,00 € en votre faveur');
  });

  it('zero bars stay hairlines: the floor applies to both and the axis spans at least one', async () => {
    const loaded = estimate();
    loaded.result.withholding.annual = '0.00';
    loaded.result.incomeTax = '0.00';
    const [annual, due] = withheldBars(i18n.t.bind(i18n), loaded);
    expect([annual!.value, due!.value]).toEqual([0.01, 0.01]);
    expect(annual!.text.replace(/\s/g, ' ')).toBe('0,00 €');
    await open(loaded);
    const card = screen.getByRole('region', { name: 'Ce que le taux devrait donner' });
    await waitFor(() => expect(card.querySelectorAll('.recharts-label-list text')).toHaveLength(2));
    const widths = [...card.querySelectorAll('.recharts-bar-rectangle path')].map((n) => Number(n.getAttribute('width')));
    expect(Math.max(...widths)).toBeLessThan(10);
  });

  it('puts the captions above the bars: no category axis, a figure that names the chart', async () => {
    await open();
    const card = screen.getByRole('region', { name: 'Ce que le taux devrait donner' });
    expect(within(card).getByRole('figure', { name: 'Prélevé sur l’année et impôt dû' })).toBeInTheDocument();
    expect(card.querySelector('.recharts-yAxis')).toBeNull();
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

  it('says the months of the year a new rate still reaches, and that the rest is next year’s (#717)', async () => {
    const late = cutOf();
    late.result.withholding.cut = { ...late.result.withholding.cut!, less: '17.89', months: 1 };
    await open(late);
    const card = screen.getByRole('region', { name: 'Baisse sans risque' });
    expect(card).toHaveTextContent('sur le dernier mois de l’année, le nouveau taux s’appliquant deux mois après la demande ; la baisse se poursuit en 2027');
  });

  it('says a new rate asked too late reaches only next year (#717)', async () => {
    const late = cutOf();
    late.result.withholding.cut = { ...late.result.withholding.cut!, less: '0.00', months: 0 };
    await open(late);
    expect(screen.getByRole('region', { name: 'Baisse sans risque' })).toHaveTextContent('rien cette année : le nouveau taux s’appliquerait deux mois après la demande, la baisse porte sur 2027');
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
