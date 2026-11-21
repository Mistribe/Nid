import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { month, spendingServer } from './testData';

const line = (i: number) => ({
  id: `t${i}`, bookedOn: '2026-11-03', valueOn: null, amount: '-42.50', currency: 'EUR', label: `CB PRIMEUR ${i}`, counterparty: null,
  fromCard: true, cardName: null, fetchedVia: 'file', linkState: 'unlinked', link: null, accountName: 'Compte joint', category: null,
  categorySource: null, accountId: 'a1', suggestedPattern: null, sharedAccount: true,
});

describe('a category’s lines', () => {
  beforeAll(async () => { await import('./SpendingPage'); await import('./SpendingOverview'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 19, 12)); });
  afterEach(() => vi.useRealTimers());

  it('opens on a row with the page’s scope and month, shows the server’s total and loads more', async () => {
    spendingServer();
    const asked: URLSearchParams[] = [];
    server.use(http.get('*/api/v1/banking/spending/lines', ({ request }) => {
      const query = new URL(request.url).searchParams;
      asked.push(query);
      const page = Number(query.get('page'));
      return HttpResponse.json({ items: page === 1 ? Array.from({ length: 50 }, (_, i) => line(i)) : [line(50)], total: '640.10', count: 51, incomplete: false, currency: 'EUR' });
    }));
    const { user, router } = await renderApp('/spending?scope=me');

    await user.click(within(await screen.findByRole('table', { name: 'Par catégorie' })).getByRole('button', { name: 'Voir les opérations : Courses' }));
    const dialog = await screen.findByRole('dialog', { name: 'Courses · novembre 2026' });
    expect(Object.fromEntries(asked[0]!)).toEqual({ scope: 'me', month: '2026-11', categoryId: '11111111-1111-1111-1111-111111111111', page: '1' });
    expect(await within(dialog).findByText(/640,10\s€/)).toBeInTheDocument();
    expect(within(dialog).getByText('51 opérations')).toBeInTheDocument();
    expect(within(dialog).getAllByRole('row')).toHaveLength(51);
    await expectNoAxeViolations(dialog);

    await user.click(within(dialog).getByRole('button', { name: 'Charger plus' }));
    await waitFor(() => expect(within(dialog).getAllByRole('row')).toHaveLength(52));
    expect(asked[1]!.get('page')).toBe('2');
    expect(within(dialog).queryByRole('button', { name: 'Charger plus' })).toBeNull();

    // #423: the same lines in the Opérations tab, over the whole month; the server adds the children.
    await user.click(within(dialog).getByRole('link', { name: 'Voir dans Opérations' }));
    await waitFor(() => expect(router.state.location.search).toEqual({
      scope: 'me', tab: 'operations', from: '2026-11-01', to: '2026-11-30', category: ['11111111-1111-1111-1111-111111111111'],
    }));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('lists the unclassified lines of the 12 months', async () => {
    spendingServer({ ...month, from: '2025-12', to: '2026-11' });
    let asked: URLSearchParams | undefined;
    server.use(http.get('*/api/v1/banking/spending/lines', ({ request }) => {
      asked = new URL(request.url).searchParams;
      return HttpResponse.json({ items: [], total: '300.00', count: 0, incomplete: true, currency: 'EUR' });
    }));
    const { user, router } = await renderApp('/spending?period=12m');

    await user.click(within(await screen.findByRole('table', { name: 'Par catégorie' })).getByRole('button', { name: 'Voir les opérations : Non classées' }));
    const dialog = await screen.findByRole('dialog', { name: 'Non classées · de décembre 2025 à novembre 2026' });
    await waitFor(() => expect(asked && Object.fromEntries(asked)).toEqual({ scope: 'household', from: '2025-12', to: '2026-11', uncategorised: 'true', page: '1' }));
    expect(await within(dialog).findByText('Aucune opération sur la période.')).toBeInTheDocument();
    expect(within(dialog).getByRole('note')).toHaveTextContent('total incomplet');

    // The figure counts the debits only: so does the list.
    await user.click(within(dialog).getByRole('link', { name: 'Voir dans Opérations' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ tab: 'operations', from: '2025-12-01', to: '2026-11-30', uncategorised: true, direction: 'out' }));
  });
});
