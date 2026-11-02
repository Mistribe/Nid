import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertiesValuation } from '../api';
import { householdValuation, PROPERTY, RENTAL } from '../testData';

const serve = (body: PropertiesValuation, seen: string[] = []) =>
  server.use(http.get('*/api/v1/realestate/properties/overview/valuation', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));

describe('Biens › valorisation', () => {
  beforeAll(async () => { await import('../PropertiesPage'); await import('./ValuationTab'); }, 30_000);

  it('shows the server figures', async () => {
    const seen: string[] = [];
    serve(householdValuation(), seen);
    const { container } = await renderApp('/properties?tab=valuation');

    expect(await screen.findByRole('tab', { name: 'Valorisation' })).toHaveAttribute('aria-selected', 'true');
    const figures = (await screen.findByText('Plus-value latente', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('+20 700,00 €+4,4 % sur 467 300,00 € de prix de revient · avant impôt');
    expect(figures).toHaveTextContent('Valeur au m²3 392,00 €85,5 m² au total1 bien sans surface n’y compte pas');
    expect(figures).toHaveTextContent('Estimations1 sur 2 à jour1 bien à réestimer');
    expect(seen).toEqual(['?scope=household']);
    await expectNoAxeViolations(container);
  });

  it('draws each property since its purchase, with a table for assistive tech', async () => {
    serve(householdValuation());
    await renderApp('/properties?tab=valuation');

    const history = await screen.findByRole('table', { name: 'Estimations de Maison de Nantes' });
    const rows = within(history).getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent)).toHaveLength(4);
    expect(rows[0]).toHaveTextContent('15 juin 2020269 000,00 €');
    expect(rows[2]).toHaveTextContent('1 juin 2025280 000,00 €');
  });

  it('lists each property with its price per m², annual pace and holding, and a stale one asks for an estimate', async () => {
    serve(householdValuation());
    await renderApp('/properties?tab=valuation');

    const table = await screen.findByRole('table', { name: 'Valorisation par bien' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Bien', 'Surface', 'Prix de revient', 'Valeur', 'Prix au m²', 'Plus-value latente', 'Rythme annuel', 'Détenu depuis', 'Dernière estimation',
    ]);
    const [home, flat] = within(table).getAllByRole('row').slice(1);
    expect(home).toHaveTextContent('85,5 m²');
    expect(home).toHaveTextContent('3 392,00 €');
    expect(home).toHaveTextContent('+9 000,00 € · +3,2 %');
    expect(home).toHaveTextContent('+0,5 %');
    expect(home).toHaveTextContent(/\d+ ans/);
    expect(within(home!).getByRole('link', { name: /Maison de Nantes/ })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(flat).toHaveTextContent('Non significatif');
    expect(within(flat!).getByRole('link', { name: 'Ajouter une estimation' })).toHaveAttribute('href', `/properties/${RENTAL}?tab=valuations`);
  });
});
