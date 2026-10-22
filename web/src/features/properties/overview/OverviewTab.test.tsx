import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertiesOverview } from '../api';
import { householdOverview, OWNER, PROPERTY, propertiesOverview, RENTAL } from '../testData';

const serve = (body: PropertiesOverview, seen: string[] = []) =>
  server.use(http.get('*/api/v1/realestate/properties/overview', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
const region = (name: string) => screen.findByRole('region', { name });

describe('Biens › vue d’ensemble', () => {
  beforeAll(async () => { await import('../PropertiesPage'); await import('./OverviewTab'); }, 30_000);

  it('opens on the overview and shows the server figures', async () => {
    const seen: string[] = [];
    serve(householdOverview(), seen);
    const { container } = await renderApp('/properties');

    expect(screen.getByRole('tab', { name: 'Vue d’ensemble' })).toHaveAttribute('aria-selected', 'true');
    const figures = (await screen.findByText('Valeur des biens', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('488 000,00 €1 estimation à mettre à jour');
    expect(figures).toHaveTextContent('Crédits restants193 564,23 €1 crédit lié');
    expect(figures).toHaveTextContent('Valeur nette294 435,77 €60,3 % de la valeur');
    expect(figures).toHaveTextContent('Plus-value latente+20 700,00 €4,4 % sur le prix de revient · avant impôt');
    expect(figures).toHaveTextContent('Loyers reçus, 12 mois9 360,00 €rendement brut 5,02 %');
    // Three years by default, out of the URL.
    expect(seen).toEqual(['?scope=household&span=3y']);
    await expectNoAxeViolations(container);
  });

  it('splits by type and by person, and lists the properties with a total', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: OWNER, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(householdOverview());
    await renderApp('/properties');

    const types = await region('Répartition par type');
    const [home, rental] = within(types).getAllByRole('row');
    expect(home).toHaveTextContent('Résidence principale290 000,00 €59,4 %');
    expect(rental).toHaveTextContent('Bien locatif198 000,00 €40,6 %');
    expect(await region('Part de chacun')).toHaveTextContent('Alice294 435,77 €100,0 % · valeur 488 000,00 € · crédits 193 564,23 €');

    const table = await screen.findByRole('table', { name: 'Vue d’ensemble des biens' });
    expect(within(table).getAllByRole('columnheader').map((h) => h.textContent))
      .toEqual(['Bien', 'Propriétaires', 'Valeur', 'Crédit restant', 'Valeur nette', 'Plus-value latente', 'Rendement brut', 'Dernière estimation']);
    const [nantes, flat] = within(table).getAllByRole('row').slice(1);
    expect(within(nantes!).getByRole('link', { name: /Maison de Nantes/ })).toHaveAttribute('href', `/properties/${PROPERTY}`);
    expect(nantes).toHaveTextContent('193 564,23 €');
    expect(nantes).toHaveTextContent('+9 000,00 €');
    expect(flat).toHaveTextContent('5,02 %');
    expect(flat).toHaveTextContent('À réestimer');
    expect(await region('Biens')).toHaveTextContent('Total · 2 biens488 000,00 € · 294 435,77 €');
    expect(screen.getByRole('link', { name: 'Voir la liste' })).toHaveAttribute('href', expect.stringContaining('tab=list'));
  });

  it('says what to watch, each line to the tab of its property where it is dealt with', async () => {
    serve(householdOverview());
    await renderApp('/properties');

    const watch = await region('À surveiller');
    const items = within(watch).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(items[0]).toHaveTextContent('T2 VilleurbanneEstimation à mettre à jour, dernière au 30 sept. 2024');
    expect(items[1]).toHaveTextContent('T2 VilleurbanneImpayés sur 12 mois : 780,00 €');
    expect(items[2]).toHaveTextContent('T2 VilleurbanneCharge prévue le 15 nov. 2026 : 1 120,00 €');
    expect(within(items[0]!).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=valuations`);
    expect(within(items[1]!).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=rental`);
    expect(within(items[2]!).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=expenses`);
  });

  it('draws the curve, then a table on demand, and keeps the span in the URL', async () => {
    const seen: string[] = [];
    serve(householdOverview(), seen);
    const { user } = await renderApp('/properties');

    const chart = await region('Valeur nette et crédits');
    await user.click(within(chart).getByRole('button', { name: 'Afficher en tableau' }));
    const rows = within(within(chart).getByRole('table')).getAllByRole('row');
    expect(rows[3]).toHaveTextContent('Aujourd’hui488 000,00 €193 564,23 €294 435,77 €');
    await user.click(within(chart).getByRole('radio', { name: 'Tout' }));
    expect(seen.at(-1)).toBe('?scope=household&span=all');
  });

  it('invites to add the first property when there is none', async () => {
    serve(propertiesOverview());
    const { container } = await renderApp('/properties');

    expect(await screen.findByRole('heading', { name: 'Aucun bien pour l’instant' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says « Inconnu » and the currency when a rate is missing', async () => {
    serve(propertiesOverview({
      count: 1, value: null, netValue: null, linkedBalance: null,
      findings: [{ code: 'core.fx_rate_missing', message: 'No CHF rate.', reference: 'CHF:2026-10-22' }],
    }));
    await renderApp('/properties');

    const figures = (await screen.findByText('Valeur des biens', { selector: 'dt' })).closest('dl')!;
    expect(figures).toHaveTextContent('Valeur des biensInconnutaux de change manquant : CHF');
  });
});
