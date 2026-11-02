import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertiesCosts } from '../api';
import { householdCosts, PROPERTY, propertiesCosts, RENTAL } from '../testData';

const serve = (body: PropertiesCosts, seen: string[] = []) =>
  server.use(http.get('*/api/v1/realestate/properties/overview/costs', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Biens › charges et coûts', () => {
  beforeAll(async () => { await import('../PropertiesPage'); await import('./CostsTab'); }, 30_000);

  it('shows the server figures', async () => {
    const seen: string[] = [];
    serve(householdCosts(), seen);
    const { container } = await renderApp('/properties?tab=costs');

    expect(await screen.findByRole('tab', { name: 'Charges et coûts' })).toHaveAttribute('aria-selected', 'true');
    const figures = (await screen.findByText('Charges payées, 12 mois', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('9 230,00 €dont travaux 1 800,00 €');
    expect(figures).toHaveTextContent('Charges prévues, 12 prochains mois7 180,00 €≈ 598,00 € par mois');
    expect(figures).toHaveTextContent('Prochaine échéance1 nov. 2026Charges de copropriété · Maison de Nantes · 360,00 €');
    expect(figures).toHaveTextContent('Coût réel depuis l’achat482 523,35 €');
    expect(seen).toEqual(['?scope=household']);
    await expectNoAxeViolations(container);
  });

  it('shows the categories paid, then planned', async () => {
    serve(householdCosts());
    const { user } = await renderApp('/properties?tab=costs');

    const card = await region('Par catégorie');
    expect(within(card).getAllByRole('row').map((r) => r.textContent)).toHaveLength(3);
    expect(within(card).getAllByRole('row')[2]).toHaveTextContent('Travaux1 800,00 €');
    await user.click(within(card).getByRole('radio', { name: 'Prévues' }));
    const rows = within(card).getAllByRole('row');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Charges de copropriété3 300,00 €');
  });

  it('groups the dues by month and lists each property with its real cost', async () => {
    serve(householdCosts());
    await renderApp('/properties?tab=costs');

    const dues = await region('Prochaines échéances');
    expect(dues).toHaveTextContent('novembre 2026');
    expect(dues).toHaveTextContent('15 nov. 2026 · Taxe foncièreT2 Villeurbanne1 140,00 €');
    expect(dues).toHaveTextContent('janvier 2027');

    const table = await screen.findByRole('table', { name: 'Coût réel de chaque bien depuis l’achat' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Bien', 'Achat et frais', 'Travaux', 'Intérêts', 'Assurance', 'Frais de prêt', 'Autres charges', 'Coût réel', 'Loyers reçus', 'Coût net des loyers',
    ]);
    const [home, flat] = within(table).getAllByRole('row').slice(1);
    expect(within(home!).getByRole('link', { name: /Maison de Nantes/ })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(home).toHaveTextContent('288 823,35 €');
    expect(flat).toHaveTextContent('167 960,00 €');
    const rows = await screen.findByRole('table', { name: 'Charges par bien' });
    expect(within(within(rows).getAllByRole('row')[2]!).getByRole('link', { name: /T2 Villeurbanne/ }))
      .toHaveAttribute('href', `/properties/${RENTAL}?tab=expenses`);
  });

  it('says when nothing is planned', async () => {
    serve(propertiesCosts({ properties: householdCosts().properties }));
    await renderApp('/properties?tab=costs');

    const dues = await region('Prochaines échéances');
    expect(dues).toHaveTextContent('Aucune charge prévue dans les 12 prochains mois.');
    expect(within(dues).getByRole('link', { name: 'Ajouter une charge prévue' })).toHaveAttribute('href', `/properties/${PROPERTY}?tab=expenses`);
  });
});
