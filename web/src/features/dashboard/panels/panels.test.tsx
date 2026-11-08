import { render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { allCapabilities } from '@/test/fixtures';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { overview, sourceRow } from '../../income/testData';
import type { DashboardView } from '@/api/dashboard';
import { allocation, allocationLine, commitment, dashboard, envelope, person } from '../testData';
import { NetWorthCharts } from './NetWorthCharts';

function serve(body: DashboardView) {
  server.use(http.get('*/api/v1/dashboard', () => HttpResponse.json(body)));
}

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const PROPERTY = '00000000-0000-7000-8000-0000000000f1';
const LOAN = '00000000-0000-7000-8000-0000000000f2';
const dialog = (name: string) => screen.findByRole('dialog', { name });

// Lazy chunks load cold on the first render; warm them so no findBy races its 1 s default.
beforeAll(async () => { await import('./FigurePanel'); await import('./NetWorthCharts'); }, 30_000);

describe('key figure panels', () => {
  it('opens the net worth panel from its figure, Escape closes it and returns focus', async () => {
    serve(dashboard());
    const { container, router, user } = await renderApp('/dashboard?scope=me');

    const tile = await screen.findByRole('button', { name: /Patrimoine net/ });
    await user.click(tile);
    const panel = await dialog('Patrimoine net');
    expect(router.state.location.search).toEqual({ scope: 'me', panel: 'net_worth' });
    expect(panel).toHaveTextContent('Valeurs au 30 septembre 2025');
    expect(within(panel).getByRole('link', { name: 'Voir les placements' })).toHaveAttribute('href', '/investments?scope=me');
    expect(within(panel).getByRole('link', { name: 'Voir les biens' })).toHaveAttribute('href', '/properties?scope=me');
    await expectNoAxeViolations(container);

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(router.state.location.search).toEqual({ scope: 'me' });
    await waitFor(() => expect(tile).toHaveFocus());
  });

  it('opens straight on the panel named by the URL, and ignores the links of a disabled module', async () => {
    serve(dashboard({ staleEnvelopes: 2, envelopesWithoutValue: 1, envelopesWithoutStart: 1 }));
    await renderApp('/dashboard?panel=net_worth', { capabilities: { ...allCapabilities, modules: ['core'] } });

    const panel = await dialog('Patrimoine net');
    expect(panel).toHaveTextContent('2 valeurs à mettre à jour');
    expect(panel).toHaveTextContent('1 élément sans valeur');
    expect(panel).toHaveTextContent('dont 1 000,00 € versés');
    expect(panel).toHaveTextContent('1 élément sans valeur récente au 1er janvier');
    expect(within(panel).queryByRole('link')).toBeNull();
  });

  it('shows the composition by type, a loan negative, each line linked and keeping the scope', async () => {
    serve(dashboard({
      envelopeSplit: [
        envelope(),
        envelope({ id: PROPERTY, module: 'properties', name: 'Maison', value: eur('310000.00'), converted: eur('310000.00') }),
        envelope({ id: LOAN, module: 'loans', name: 'Prêt travaux', value: eur('-2000.00'), converted: eur('-2000.00'), asOf: '2025-10-15' }),
        envelope({ id: '00000000-0000-7000-8000-0000000000e4', name: 'Vide', value: null, converted: null, asOf: null }),
      ],
    }));
    const { container, router, user } = await renderApp('/dashboard?scope=me&panel=net_worth');

    const panel = await dialog('Patrimoine net');
    const table = within(panel).getByRole('table', { name: 'Valeur de chaque élément' });
    expect(within(table).getAllByRole('rowheader').map((h) => h.textContent)).toEqual(['PEA', 'Vide', 'Maison', 'Prêt travaux']);
    expect(within(table).getAllByRole('columnheader', { name: /Placements|Biens|Crédits/ }).map((h) => h.textContent)).toEqual(['Placements', 'Biens', 'Crédits']);
    const rows = within(table).getAllByRole('row');
    expect(rows.find((r) => r.textContent?.includes('PEA'))).toHaveTextContent('25 000,00 €');
    expect(rows.find((r) => r.textContent?.includes('Maison'))).toHaveTextContent('310 000,00 €');
    expect(rows.find((r) => r.textContent?.includes('Prêt travaux'))).toHaveTextContent('-2 000,00 €');
    const empty = rows.find((r) => r.textContent?.includes('Vide'));
    expect(empty).toHaveTextContent('Sans valeur');
    expect(empty).not.toHaveTextContent('0,00 €');
    expect(within(panel).getByRole('link', { name: 'Maison' })).toHaveAttribute('href', `/properties/${PROPERTY}?scope=me`);
    expect(within(panel).getByRole('link', { name: 'Prêt travaux' })).toHaveAttribute('href', `/loans/${LOAN}?scope=me`);
    await expectNoAxeViolations(container);

    await user.click(within(panel).getByRole('link', { name: 'PEA' }));
    expect(router.state.location.pathname).toBe('/investments/00000000-0000-7000-8000-0000000000e1');
    expect(router.state.location.search).toEqual({ scope: 'me' });
  });

  it('says what is estimated: the count on the tile and each row, with its date and what is stale', async () => {
    serve(dashboard({
      estimatedEnvelopes: 2, staleEnvelopes: 1,
      envelopeSplit: [
        envelope({ name: 'PEA', estimated: true, asOf: '2025-10-12', valuedOn: '2025-09-30' }),
        envelope({ id: '00000000-0000-7000-8000-0000000000e3', name: 'Livret A', estimated: true, stale: true, asOf: '2025-10-12', valuedOn: '2025-06-03' }),
        envelope({ id: '00000000-0000-7000-8000-0000000000e4', name: 'Compte' }),
      ],
    }));
    const { container } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    expect(panel).toHaveTextContent('2 valeurs estimées');
    const rows = within(panel).getAllByRole('row');
    expect(panel).toHaveTextContent('1 valeur à mettre à jour');
    expect(rows.find((r) => r.textContent?.includes('PEA'))).toHaveTextContent('12 octobre 2025estimée');
    expect(rows.find((r) => r.textContent?.includes('Livret A'))).toHaveTextContent('estimée, valeur du 3 juin 2025 à mettre à jour');
    expect(rows.find((r) => r.textContent?.includes('Compte'))).not.toHaveTextContent('estimée');
    await expectNoAxeViolations(container);
  });

  it('says nothing about estimates when none is counted', async () => {
    serve(dashboard({ envelopeSplit: [envelope()] }));
    await renderApp('/dashboard?panel=net_worth');

    expect(await dialog('Patrimoine net')).not.toHaveTextContent('estimée');
  });

  it('flags a stale value in amber with its date and links to the routine', async () => {
    serve(dashboard({
      staleEnvelopes: 1,
      envelopeSplit: [envelope({ id: '00000000-0000-7000-8000-0000000000e3', name: 'Livret A', converted: eur('900.00'), stale: true, asOf: '2025-06-03' })],
    }));
    const { user, router } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    const row = within(panel).getAllByRole('row').find((r) => r.textContent?.includes('Livret A'))!;
    expect(row).toHaveTextContent('À mettre à jour');
    expect(row).toHaveTextContent('3 juin 2025');
    expect(row.querySelector('.text-status-warning-text')).not.toBeNull();
    await user.click(within(panel).getByRole('link', { name: '1 valeur à mettre à jour' }));
    expect(router.state.location.pathname).toBe('/routine');
  });

  it('says « inconnue » for an envelope whose rate is missing and keeps its own amount', async () => {
    serve(dashboard({
      envelopeSplit: [envelope({ name: 'Dollars', value: { amount: '100.00', currency: 'USD' }, converted: null, missingCurrency: 'USD' })],
      people: null, peopleMissingCurrency: 'USD',
    }));
    const { user } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    expect(panel).toHaveTextContent('Inconnue : taux de change manquant (USD) · 100,00 $US');
    await user.click(within(panel).getByRole('radio', { name: 'Par personne' }));
    expect(panel).toHaveTextContent('Inconnue : taux de change manquant (USD)');
  });

  it('shows each person’s share by person', async () => {
    serve(dashboard({
      envelopeSplit: [envelope()],
      people: [person(), person({ id: '00000000-0000-7000-8000-0000000000a2', name: 'Bob', slot: 1 })],
    }));
    const { container, user } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    await user.click(within(panel).getByRole('radio', { name: 'Par personne' }));
    const rows = within(within(panel).getByRole('table', { name: 'Part de chaque personne' })).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Alice');
    expect(rows[1]).toHaveTextContent('12 500,00 €');
    expect(rows[1]).toHaveTextContent('50 %');
    expect(rows[2]).toHaveTextContent('Bob');
    expect(await within(panel).findByRole('figure', { name: 'Graphique de la part de chaque personne' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows the assets by class with the debts apart and the guessed part, then by liquidity', async () => {
    serve(dashboard({ envelopeSplit: [envelope()], allocation: allocation() }));
    const { container, user } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    await user.click(within(panel).getByRole('radio', { name: 'Par classe' }));
    const rows = within(within(panel).getByRole('table', { name: 'Actifs par classe' })).getAllByRole('row');
    expect(rows.map((r) => r.querySelector('th')?.textContent)).toEqual(['Classe d’actifs', 'Actions', 'Fonds en euros et garantis', 'Immobilier']);
    expect(rows[3]).toHaveTextContent('300 000,00 €');
    expect(rows[3]).toHaveTextContent('92,3 %');
    expect(panel).not.toHaveTextContent('Cible');
    expect(panel).toHaveTextContent('Crédits restant dus : -2 000,00 €, déduits à part.');
    expect(panel).toHaveTextContent('Dont 9 375,00 € classés d’après le type de support ou d’enveloppe');
    expect(await within(panel).findByRole('figure', { name: 'Graphique de la répartition des actifs' })).toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(within(panel).getByRole('radio', { name: 'Par disponibilité' }));
    const liquidity = within(within(panel).getByRole('table', { name: 'Actifs par disponibilité' })).getAllByRole('row');
    expect(liquidity[1]).toHaveTextContent('Disponible tout de suite');
    expect(liquidity[2]).toHaveTextContent('Bloqué (retraite, vente d’un bien)');
    expect(within(panel).queryByRole('button', { name: 'Définir une cible' })).toBeNull();
  });

  it('shows the target and the gaps, and sends a new target that adds up to 100 %', async () => {
    serve(dashboard({
      envelopeSplit: [envelope()],
      allocation: allocation({
        target: { equity: 60, bond: 40 },
        classes: [
          allocationLine('bond', '0.00', 0, { targetPct: 40, gapPct: -40, gap: { amount: '-130000.00', currency: 'EUR' } }),
          allocationLine('equity', '9375.00', 2.9, { targetPct: 60, gapPct: -57.1, gap: { amount: '-185625.00', currency: 'EUR' } }),
          allocationLine('real_estate', '300000.00', 92.3),
        ],
      }),
    }));
    let sent: unknown;
    server.use(http.put('*/api/v1/dashboard/allocation-target', async ({ request }) => {
      const body = (await request.json()) as { target: Record<string, number> };
      sent = body;
      return HttpResponse.json(body);
    }));
    const { user } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    await user.click(within(panel).getByRole('radio', { name: 'Par classe' }));
    const equity = within(panel).getAllByRole('row').find((r) => r.textContent?.startsWith('Actions'))!;
    expect(equity).toHaveTextContent('Cible 60 % · écart -57,1 pts, -185 625,00 €');
    expect(within(panel).getAllByRole('row').find((r) => r.textContent?.startsWith('Immobilier'))).toHaveTextContent('Pas de cible');

    await user.click(within(panel).getByRole('button', { name: 'Modifier la cible' }));
    const form = await dialog('Répartition cible du foyer');
    expect(within(form).getByRole('textbox', { name: 'Actions' })).toHaveValue('60');
    await user.clear(within(form).getByRole('textbox', { name: 'Obligations' }));
    await user.type(within(form).getByRole('textbox', { name: 'Obligations' }), '30');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la cible' }));
    expect(within(form).getByRole('alert')).toHaveTextContent('Chaque part va de 0 à 100 %, et le total fait 100 %.');
    expect(sent).toBeUndefined();

    await user.type(within(form).getByRole('textbox', { name: 'Immobilier' }), '10,0');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer la cible' }));
    await waitFor(() => expect(sent).toEqual({ target: { bond: 30, equity: 60, real_estate: 10 } }));
    expect(await screen.findByText('Cible enregistrée.')).toBeInTheDocument();
  });

  it('says why there is no allocation when a rate is missing', async () => {
    serve(dashboard({ envelopeSplit: [envelope()], allocation: null, people: null, peopleMissingCurrency: 'USD' }));
    const { user } = await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    await user.click(within(panel).getByRole('radio', { name: 'Par classe' }));
    expect(panel).toHaveTextContent('Inconnue : taux de change manquant (USD)');
  });

  it('shows no composition without an envelope', async () => {
    serve(dashboard({ envelopes: 0, envelopeSplit: [], people: [] }));
    await renderApp('/dashboard?panel=net_worth');

    const panel = await dialog('Patrimoine net');
    expect(within(panel).queryByRole('table')).toBeNull();
    expect(within(panel).queryByRole('radio')).toBeNull();
  });

  it('lists the income sources with their monthly equivalent and owners', async () => {
    serve(dashboard());
    server.use(
      http.get('*/api/v1/core/persons', () => HttpResponse.json([{ id: '01926b9e-0000-7000-8000-00000000000a', kind: 'adult', displayName: 'Alice Dev', email: null }])),
      http.get('*/api/v1/income/overview', () => HttpResponse.json(overview({
        sources: [
          sourceRow({ owners: [{ personId: '01926b9e-0000-7000-8000-00000000000a', shareBp: 10000 }] }),
          sourceRow({ id: PROPERTY, module: 'realestate', name: 'Studio Lyon', kind: 'rent', perMonth: eur('800.00') }),
        ],
      }))),
    );
    const { container, user } = await renderApp('/dashboard?panel=income');

    const panel = await dialog('Revenus nets / mois');
    const item = (await within(panel).findByRole('link', { name: 'Salaire Alice' })).closest('li')!;
    expect(item).toHaveTextContent('2 450,00 €');
    expect(await within(item).findByText('Alice Dev')).toBeInTheDocument();
    const rent = within(panel).getByRole('link', { name: 'Studio Lyon' });
    expect(rent).toHaveAttribute('href', `/properties/${PROPERTY}?tab=rental`);
    expect(rent.closest('li')).toHaveTextContent('800,00 €');
    expect(within(panel).getByRole('link', { name: 'Voir les revenus' })).toHaveAttribute('href', '/income');
    await expectNoAxeViolations(container);
    await user.keyboard('{Escape}');
  });

  it('an estimated childcare source says so', async () => {
    serve(dashboard({ commitmentSources: [{ ...commitment('childcare', '660.47'), estimated: true }] }));
    await renderApp('/dashboard?panel=commitments');
    expect(await dialog('Engagements / mois')).toHaveTextContent('Garde 660,47 € (estimation)');
  });

  it('lists the commitment sources with links to subscriptions and loans', async () => {
    serve(dashboard({
      commitmentSources: [commitment('subscriptions', '250.56'), commitment('loans', '800.00'), commitment('property_costs', '90.00')],
    }));
    const { container } = await renderApp('/dashboard?panel=commitments');

    const panel = await dialog('Engagements / mois');
    expect(panel).toHaveTextContent('Abonnements 250,56 €');
    expect(panel).toHaveTextContent('Crédits 800,00 €');
    expect(panel).toHaveTextContent('Charges des biens 90,00 €');
    expect(panel).toHaveTextContent('10 % des revenus');
    expect(within(panel).getByRole('link', { name: 'Voir les abonnements' })).toHaveAttribute('href', '/subscriptions');
    expect(within(panel).getByRole('link', { name: 'Voir les crédits' })).toHaveAttribute('href', '/loans');
    await expectNoAxeViolations(container);
  });

  it('repeats the month split in the margin panel', async () => {
    serve(dashboard());
    const { container } = await renderApp('/dashboard?panel=margin');

    const panel = await dialog('Reste à vivre');
    expect(panel).toHaveTextContent('2 249,45 €');
    expect(within(panel).getByRole('region', { name: 'Où part le revenu du mois' })).toHaveTextContent('Reste 2 249,45 €');
    await expectNoAxeViolations(container);
  });
});

describe('net worth charts', () => {
  it('sizes the envelope chart by its rows', () => {
    const rows = Array.from({ length: 12 }, (_, i) => envelope({ id: `00000000-0000-7000-8000-0000000001${String(i).padStart(2, '0')}`, name: `E${i}` }));
    render(<NetWorthCharts view="type" rows={rows} people={[]} />);
    const figure = screen.getByRole('figure', { name: 'Graphique de la valeur de chaque élément' });
    expect(figure.querySelector('[data-chart]')).toHaveStyle({ height: `${12 * 32 + 16}px` });
  });

  it('draws nothing when no envelope has a converted value', () => {
    render(<NetWorthCharts view="type" rows={[envelope({ converted: null, value: null })]} people={[]} />);
    expect(screen.queryByRole('figure')).not.toBeInTheDocument();
  });

  it('draws no people chart when a share is negative', () => {
    render(<NetWorthCharts view="person" rows={[]} people={[person(), person({ id: '00000000-0000-7000-8000-0000000000a2', name: 'Bob', slot: 1, amount: eur('-2000.00'), pct: -19 })]} />);
    expect(screen.queryByRole('figure')).not.toBeInTheDocument();
  });
});
