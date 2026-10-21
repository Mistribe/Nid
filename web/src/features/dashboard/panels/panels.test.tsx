import { render, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { allCapabilities } from '@/test/fixtures';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { overview, sourceRow } from '../../income/testData';
import type { DashboardView } from '../api';
import { commitment, dashboard, envelope, person } from '../testData';
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

    const panel = await dialog('Revenus / mois');
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
