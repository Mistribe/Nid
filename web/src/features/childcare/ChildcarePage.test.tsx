import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { ChildcareList } from './api';
import { detail, eur, list, summary } from './testData';
import { expectOnePrimary } from '@/test/headerActions';

function serve(body: ChildcareList, seen: string[] = []) {
  server.use(http.get('*/api/v1/childcare', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
}

async function rows(): Promise<HTMLElement[]> {
  const table = await screen.findByRole('table', { name: 'Liste des gardes' });
  return within(table).getAllByRole('row').slice(1);
}

describe('ChildcarePage', () => {
  beforeAll(async () => { await import('./ChildcarePage'); }, 30_000);

  it('opens on the overview of the current arrangement', async () => {
    const seen: string[] = [];
    serve(list([summary({ id: 'a', name: 'Ancienne garde', closed: true, end: '2025-06-30' }), summary({ toPrepare: ['2025-09'] })]), seen);
    server.use(http.get('*/api/v1/childcare/:id', () => HttpResponse.json(detail({ cmgEstimate: eur('300.00'), monthCost: eur('560.00'), monthCostEstimated: true }))));
    const { container } = await renderApp('/childcare');
    const hero = await screen.findByRole('region', { name: 'Salaire mensualisé · Garde de Léa' });
    expect(hero).toHaveTextContent('626,37');
    expect(await within(hero).findByText('3 jours')).toBeInTheDocument();
    expect(within(hero).getByText('300,00 €')).toBeInTheDocument();
    // #723: the dashboard's « Garde », named the same way, an estimate until the month is validated.
    expect(hero).toHaveTextContent(/Coût net du mois.*560,00\s€.*estimation/);
    expect(within(hero).getByRole('link', { name: 'Ouvrir la fiche' })).toHaveAttribute('href', `/childcare/${summary().id}`);
    const months = screen.getByRole('region', { name: 'Mois à préparer · Garde de Léa' });
    expect(within(months).getByRole('link', { name: /septembre 2025/ })).toHaveAttribute('href', `/childcare/${summary().id}/statements/2025-09`);
    expect(screen.queryByText('Ancienne garde')).not.toBeInTheDocument();
    expect(seen[0]).toContain('closed=true');
    expectOnePrimary(container);
    await expectNoAxeViolations(container);
  });

  it('lists every arrangement under « Liste »', async () => {
    serve(list([summary({ id: 'a', name: 'Ancienne garde', closed: true, end: '2025-06-30' }), summary()]));
    await renderApp('/childcare?tab=list');
    expect(await rows()).toHaveLength(2);
  });

  it('folds the arrangements of the same owners into one row with the server total; the switch lists them flat, kept in the URL', async () => {
    const alice = [{ personId: '0192f0aa-0000-7000-8000-0000000000a1', shareBp: 10000 }];
    const other = summary({ id: 'c', name: 'Garde de Tom' });
    serve(list([summary({ id: 'a', name: 'Ancienne garde', closed: true, end: '2025-06-30', owners: alice }), summary({ owners: alice }), other], [
      { owners: alice, items: ['a', summary().id], monthlyBasePay: eur('626.37') },
      { owners: other.owners, items: ['c'], monthlyBasePay: other.monthlyBasePay },
    ]));
    const { user, router } = await renderApp('/childcare?tab=list');

    expect(await rows()).toHaveLength(2);
    const [group] = await rows();
    expect(group).toHaveTextContent('2 gardes');
    expect(group).toHaveTextContent('626,37');
    await user.click(within(group!).getByRole('button', { name: 'Déplier les gardes' }));
    const names = async () => (await rows()).map((r) => within(r).getAllByRole('cell')[0]!.textContent);
    expect(await names()).toEqual(['2 gardes', 'Ancienne garde', 'Garde de Léa', 'Garde de Tom']);

    const grouping = screen.getByRole('switch', { name: 'Regrouper par propriétaire' });
    expect(grouping).toBeChecked();
    await user.click(grouping);
    await waitFor(() => expect(router.state.location.search).toMatchObject({ flat: 1 }));
    expect(await names()).toEqual(['Ancienne garde', 'Garde de Léa', 'Garde de Tom']);
    await user.click(screen.getByRole('switch', { name: 'Regrouper par propriétaire' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('flat'));
    expect(await rows()).toHaveLength(2);
  });

  it('without a current one, lists the closed arrangements', async () => {
    serve(list([summary({ closed: true, end: '2025-06-30', toPrepare: ['2025-06'] })]));
    const { container } = await renderApp('/childcare');
    const [row] = await rows();
    expect(row).toHaveTextContent('Léa');
    expect(row).toHaveTextContent('Martine');
    expect(row).toHaveTextContent('626,37');
    expect(row).toHaveTextContent('1 mois');
    expect(within(row!).getByRole('link', { name: 'Garde de Léa' })).toHaveAttribute('href', `/childcare/${summary().id}`);
    expect(screen.getByRole('link', { name: 'Nouvelle garde' })).toHaveAttribute('href', '/childcare/new');
    await expectNoAxeViolations(container);
  });

  it('empty state links to creation', async () => {
    serve(list([]));
    const { container } = await renderApp('/childcare');
    expect(await screen.findByText('Aucune garde')).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Nouvelle garde' }).length).toBeGreaterThan(0);
    expectOnePrimary(container);
  });

  it('lists the nannies at home with their net cost this year, without a childminder', async () => {
    serve(list([]));
    server.use(http.get('*/api/v1/childcare/nannies', () => HttpResponse.json({ items: [
      { id: 'n1', name: 'Garde à domicile', owners: [], visibility: 'shared', closed: false, writable: true, netCost: eur('1300.00'), months: 2 },
    ] })));
    const { container } = await renderApp('/childcare');
    const table = await screen.findByRole('table', { name: 'Garde à domicile' });
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('1 300,00 €');
    expect(within(table).getByRole('link', { name: 'Garde à domicile' })).toHaveAttribute('href', '/childcare/nannies/n1');
    expect(screen.queryByText('Aucune garde')).not.toBeInTheDocument();
    expectOnePrimary(container);
  });
});
