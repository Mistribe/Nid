import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { PropertiesOverview } from '@/api/properties';
import { householdOverview, OWNER, PROPERTY, propertiesOverview, RENTAL } from '../testData';
import { expectOnePrimary } from '@/test/headerActions';

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
    expect(await region('Valeur nette')).toHaveTextContent(
      'Valeur nette294 435,77 €+20 700,00 €4,4 %en hausseplus-value latente sur le prix de revient · avant impôt60,3 % de la valeur');
    expect(figures).toHaveTextContent('Loyers reçus, 12 mois9 360,00 €rendement brut 5,02 %');
    // Three years by default, out of the URL.
    expect(seen).toEqual(['?scope=household&span=3y']);
    await expectNoAxeViolations(container);
  });

  it('leads with the hero: the net value as the page’s first figure', async () => {
    serve(householdOverview());
    await renderApp('/properties');

    const hero = await region('Valeur nette');
    expect(within(hero).getByText('294 435,77 €')).toHaveClass('text-hero', 'tabular-nums');
    expect(hero.querySelector('svg[aria-hidden="true"] path[stroke]')).toHaveAttribute('stroke', 'var(--primary)');
    const figures = (await screen.findByText('Valeur des biens', { selector: 'dt' })).closest('dl')!;
    expect(figures).not.toHaveTextContent('Valeur nette');
    expect(figures).not.toHaveTextContent('294 435,77 €');
    expect(figures).not.toHaveTextContent('Plus-value latente');
  });

  it('shows the latent gain with its arrow, the figure uncoloured', async () => {
    serve(householdOverview());
    await renderApp('/properties');

    const gain = within(await region('Valeur nette')).getByText('en hausse').parentElement!;
    expect(gain.className).not.toMatch(/text-status-good-text|text-destructive/);
    expect(gain.querySelector('svg')).toHaveClass('text-status-good');
    expect(within(gain).getByText('en hausse')).toBeInTheDocument();
  });

  it('keeps the simple view to the figures, the curve and what to watch; « Analyse » has the rest (issue #20)', async () => {
    const seen: string[] = [];
    serve(householdOverview(), seen);
    const { user, container } = await renderApp('/properties');

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent).slice(0, 2)).toEqual(['Vue d’ensemble', 'Analyse']);
    await region('À surveiller');
    expect(screen.getByRole('region', { name: 'Valeur nette et crédits' })).toBeInTheDocument();
    for (const name of ['Répartition par type', 'Part de chacun', 'Biens'])
      expect(screen.queryByRole('region', { name })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Analyse' }));
    for (const name of ['Répartition par type', 'Part de chacun', 'Biens']) expect(await region(name)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'À surveiller' })).not.toBeInTheDocument();
    // Both tabs read the one answer.
    expect(seen).toEqual(['?scope=household&span=3y']);
    await expectNoAxeViolations(container);
  });

  it('splits by type and by person, and lists the properties with a total', async () => {
    server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: OWNER, kind: 'adult', displayName: 'Alice', email: null }])));
    serve(householdOverview());
    await renderApp('/properties?tab=analysis');

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
    expect(items[0]).toHaveTextContent('T2 Villeurbanne Estimation à mettre à jour, dernière au 30 sept. 2024');
    expect(items[1]).toHaveTextContent('T2 Villeurbanne Impayés : 780,00 €');
    expect(items[2]).toHaveTextContent('T2 Villeurbanne Charge prévue le 15 nov. 2026 : 1 120,00 €');
    expect(within(items[0]!).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=valuations`);
    expect(within(items[1]!).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=rental`);
    expect(within(items[2]!).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=expenses`);
  });

  it('says the four rent revision findings, each to the rental tab', async () => {
    const line = (code: string, on: string | null, amount: string | null) => ({ code, propertyId: RENTAL, loanId: null, on, amount: amount ? { amount, currency: 'EUR' } : null });
    serve({ ...householdOverview(),
      watch: [line('revision_due', '2026-09-01', '919.84'), line('revision_lost', '2025-09-01', null), line('irl_missing', null, null), line('revision_forbidden_dpe', null, null)],
    });
    const { user } = await renderApp('/properties');

    // Three lines on the simple view, the fourth on demand (issue #20).
    const watch = await region('À surveiller');
    expect(within(watch).getAllByRole('listitem')).toHaveLength(3);
    await user.click(within(watch).getByRole('button', { name: 'Tout voir (4)' }));
    const items = within(watch).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Révision possible depuis le 1 sept. 2026 : nouveau loyer 919,84 €');
    expect(items[1]).toHaveTextContent('Révision du 1 sept. 2025 non faite');
    expect(items[2]).toHaveTextContent('IRL manquant');
    expect(items[3]).toHaveTextContent('Révision interdite (DPE F/G)');
    for (const item of items) expect(within(item).getByRole('link')).toHaveAttribute('href', `/properties/${RENTAL}?tab=rental`);
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
    expectOnePrimary(container);
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
    const hero = await region('Valeur nette');
    expect(hero.querySelector('.text-hero')).toHaveTextContent(/^Inconnutaux de change manquant : CHF$/);
    expect(within(hero.querySelector<HTMLElement>('.text-hero')!).getByText('Inconnu').closest('.text-muted-foreground'))
      .toHaveClass('text-sm', 'font-normal');
  });
});
