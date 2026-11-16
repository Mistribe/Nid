import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DashboardEvent } from '@/api/dashboard';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { event } from '../dashboard/testData';

function serve(events: DashboardEvent[] = []) {
  const seen: URLSearchParams[] = [];
  server.use(http.get('*/api/v1/calendar', ({ request }) => {
    const query = new URL(request.url).searchParams;
    seen.push(query);
    return HttpResponse.json({ from: query.get('from'), to: query.get('to'), events });
  }));
  return seen;
}
const grid = () => screen.findByRole('group', { name: 'Calendrier du mois' });

describe('Calendrier', () => {
  beforeAll(async () => { await import('./CalendarPage'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 15, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('shows the current month from its first day, each event with its words and a link to its item', async () => {
    const seen = serve([
      event({ on: '2026-11-03', name: 'Amazon Prime' }), // before today: the month is read from its 1st
      event({ on: '2026-11-20', kind: 'tax_balance_due', module: 'tax', id: null, name: 'Foyer', label: null, amount: null }),
    ]);
    const { container } = await renderApp('/calendar');

    expect(await screen.findByRole('heading', { name: 'novembre 2026' })).toBeInTheDocument();
    await waitFor(() => expect(seen.at(-1)?.toString()).toBe('scope=household&from=2026-11-01&to=2026-11-30'));
    const third = within(await grid()).getByRole('group', { name: 'mardi 3 novembre' });
    await waitFor(() => expect(within(third).getByRole('link')).toHaveTextContent('Renouvellement · Amazon Prime'));
    expect(within(third).getByRole('link')).toHaveAttribute('href', '/subscriptions/00000000-0000-7000-8000-0000000000c1');
    // A tax date is about no item: it leads to the tax page, as « À venir » does.
    expect(within(within(await grid()).getByRole('group', { name: 'vendredi 20 novembre' })).getByRole('link')).toHaveAttribute('href', '/tax');
    expect(within(await grid()).getByRole('group', { name: 'dimanche 15 novembre' })).toHaveAttribute('aria-current', 'date');
    // The phone's list of days holds the same events.
    expect(within(screen.getByRole('list', { name: 'Échéances jour par jour' })).getAllByRole('link')).toHaveLength(2);
    // No month before the current one.
    expect(screen.getByRole('button', { name: 'Mois précédent' })).toBeDisabled();
    await expectNoAxeViolations(container);
  });

  it('keeps the month and the modules in the address, and asks the server for them', async () => {
    const seen = serve();
    const { user, router } = await renderApp('/calendar?scope=me');

    await user.click(await screen.findByRole('button', { name: 'Mois suivant' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ scope: 'me', month: '2026-12' }));
    await waitFor(() => expect(seen.at(-1)?.get('to')).toBe('2026-12-31'));
    expect(seen.at(-1)?.get('scope')).toBe('me');
    expect(seen.at(-1)?.has('modules')).toBe(false); // every module

    await user.click(screen.getByRole('checkbox', { name: 'Abonnements' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ scope: 'me', month: '2026-12', modules: 'loans,properties,investments,income,tax' }));
    await waitFor(() => expect(seen.at(-1)?.get('modules')).toBe('loans,properties,investments,income,tax'));
    expect(screen.getByRole('button', { name: 'Mois précédent' })).toBeEnabled();
  });

  it('keeps the last module checked', async () => {
    serve();
    await renderApp('/calendar?modules=tax,unknown');

    expect(await screen.findByRole('checkbox', { name: 'Impôts' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Abonnements' })).not.toBeChecked();
  });
});
