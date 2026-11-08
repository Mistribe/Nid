import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PassiveIncome } from '@/api/investments';
import { passiveIncome, PEA, RENTAL } from '../testData';

const serve = (body: PassiveIncome, seen: string[] = []) =>
  server.use(http.get('*/api/v1/investments/overview/income', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));

describe('Placements › revenus', () => {
  beforeAll(async () => { await import('../EnvelopesPage'); await import('./IncomeTab'); }, 30_000);

  it('shows the server figures', async () => {
    const seen: string[] = [];
    serve(passiveIncome(), seen);
    const { container } = await renderApp('/investments?tab=income');

    const figures = (await screen.findByText('Sur 12 mois')).closest('dl')!;
    expect(figures).toHaveTextContent('Sur 12 mois2 400,00 €');
    expect(figures).toHaveTextContent('Rendement3,2 %');
    expect(figures).toHaveTextContent('Estimation 12 prochains mois2 520,00 €');
    expect(figures).toHaveTextContent('Dividendes900,00 €');
    expect(figures).toHaveTextContent('Loyers nets1 500,00 €');
    expect(figures).not.toHaveTextContent('Intérêts');
    expect(figures).toHaveTextContent('d’après les 12 derniers mois');
    expect(seen).toEqual(['?scope=household']);
    await expectNoAxeViolations(container);
  });

  it('lists the rows and links each to its envelope or property', async () => {
    serve(passiveIncome());
    await renderApp('/investments?tab=income');

    const table = await screen.findByRole('table', { name: 'Ce qui a rapporté' });
    expect(within(table).getByRole('link', { name: 'PEA' })).toHaveAttribute('href', `/investments/${PEA}`);
    expect(within(table).getByRole('link', { name: 'Studio Lyon' })).toHaveAttribute('href', `/properties/${RENTAL}`);
    expect(table).toHaveTextContent('Bien locatif');
    const cell = (row: string, column: string) => {
      const col = within(table).getAllByRole('columnheader').findIndex((h) => h.textContent === column);
      return within(within(table).getByRole('link', { name: row }).closest('tr')!).getAllByRole('cell')[col]!;
    };
    expect(cell('PEA', 'Dividendes')).toHaveTextContent('900,00 €');
    expect(cell('Studio Lyon', 'Loyers nets')).toHaveTextContent('1 500,00 €');
    expect(cell('PEA', 'Loyers nets')).toHaveTextContent('—');
    expect(within(table).getByRole('columnheader', { name: 'Rendement' })).toBeInTheDocument();
  });

  it('marks the current month as partial', async () => {
    serve(passiveIncome());
    const { user } = await renderApp('/investments?tab=income');

    expect(await screen.findByText('octobre 2025 est en cours : ses revenus ne sont pas complets.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Afficher en tableau' }));
    expect(within(await screen.findByRole('table', { name: 'Revenus de chaque mois' })).getByText('octobre 2025 (en cours)')).toBeInTheDocument();
  });

  it('names the savings accounts left out', async () => {
    serve(passiveIncome());
    await renderApp('/investments?tab=income');
    expect(await screen.findByText('Non comptés : Livret A (relié à la banque).')).toBeInTheDocument();
  });

  it('names the currencies without a rate', async () => {
    serve(passiveIncome({ findings: [{ code: 'core.fx_rate_missing', message: 'No USD rate.', reference: 'USD:2025-10-15' }] }));
    await renderApp('/investments?tab=income');
    expect(await screen.findByText('Taux de change manquant : USD')).toBeInTheDocument();
  });

  it('says how to get income when there is none', async () => {
    serve(passiveIncome({ rows: [], total: null, yieldPct: null, excluded: [] }));
    await renderApp('/investments?tab=income');
    expect(await screen.findByText(/Aucun revenu sur 12 mois\. Il en apparaît dès que vous classez un retrait en dividende/)).toBeInTheDocument();
  });

  it('asks for the reader’s scope', async () => {
    const seen: string[] = [];
    serve(passiveIncome(), seen);
    await renderApp('/investments?tab=income&scope=me');
    await screen.findByText('Sur 12 mois');
    expect(seen).toEqual(['?scope=me']);
  });
});
