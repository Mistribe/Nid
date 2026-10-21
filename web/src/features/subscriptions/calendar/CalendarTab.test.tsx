import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { Charges } from '../api';
import { eur } from '../testData';

const SPOTIFY = '0192f0aa-0000-7000-8000-0000000000d1';
type Line = Charges['charges'][number];

const line = (date: string, displayName: string, over: Partial<Line> = {}): Line =>
  ({ date, itemId: SPOTIFY, displayName, provider: null, price: eur('9.99'), converted: null, ...over });
const charges = (over: Partial<Charges> = {}): Charges =>
  ({ month: '2026-11', total: eur('0.00'), convertedIsEstimate: false, charges: [], findings: [], ...over });

function serve(body: Charges, seen: string[] = []) {
  server.use(http.get('*/api/v1/subscriptions/charges', ({ request }) => {
    seen.push(new URL(request.url).search);
    return HttpResponse.json(body);
  }));
  return seen;
}
const month = (seen: string[]) => new URLSearchParams(seen.at(-1)).get('month');
const grid = () => screen.findByRole('group', { name: 'Calendrier du mois' });
const day = async (label: string) => within(await grid()).getByRole('group', { name: label });

describe('Abonnements › calendrier', () => {
  beforeAll(async () => { await import('../SubscriptionsPage'); await import('./CalendarTab'); }, 30_000);
  beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date(2026, 10, 15, 12)); });
  afterEach(() => { vi.useRealTimers(); });

  it('It_asks_for_the_month_in_the_address', async () => {
    const seen = serve(charges({ month: '2027-01', total: eur('42.00'), charges: [line('2027-01-03', 'Spotify')] }));
    const { container } = await renderApp('/subscriptions?tab=calendar&month=2027-01');

    expect(await screen.findByRole('heading', { name: 'janvier 2027' })).toBeInTheDocument();
    expect(month(seen)).toBe('2027-01');
    const figures = (await screen.findByText('Total du mois')).closest('dl')!;
    expect(figures).toHaveTextContent('42,00 €');
    expect(figures).toHaveTextContent('Prélèvements1');
    await expectNoAxeViolations(container);
  });

  it('An_invalid_month_falls_back_to_the_current_month', async () => {
    const seen = serve(charges());
    await renderApp('/subscriptions?tab=calendar&month=2026-13');

    expect(await screen.findByRole('heading', { name: 'novembre 2026' })).toBeInTheDocument();
    expect(month(seen)).toBe('2026-11');
  });

  it('Three_charges_or_fewer_show_in_full_and_more_show_plus_n', async () => {
    serve(charges({ charges: [
      ...['A', 'B', 'C'].map((n) => line('2026-11-03', n)),
      ...['D', 'E', 'F', 'G'].map((n) => line('2026-11-10', n)),
    ] }));
    await renderApp('/subscriptions?tab=calendar');

    await waitFor(async () => expect(within(await day('mardi 3 novembre')).getAllByRole('link')).toHaveLength(3));
    const crowded = await day('mardi 10 novembre');
    expect(within(crowded).getAllByRole('link').map((a) => a.textContent)).toEqual([expect.stringContaining('D'), expect.stringContaining('E')]);
    expect(within(crowded).getByRole('button', { name: '+2 autres prélèvements' })).toHaveTextContent(/^\+2$/);
  });

  it('Plus_n_opens_the_days_list', async () => {
    serve(charges({ charges: ['D', 'E', 'F', 'G'].map((n) => line('2026-11-10', n)) }));
    const { user } = await renderApp('/subscriptions?tab=calendar');

    await waitFor(async () => expect(within(await day('mardi 10 novembre')).getByRole('button', { name: '+2 autres prélèvements' })).toBeInTheDocument());
    await user.click(within(await day('mardi 10 novembre')).getByRole('button', { name: '+2 autres prélèvements' }));
    const list = await screen.findByRole('dialog', { name: 'mardi 10 novembre' });
    expect(within(list).getAllByRole('link').map((a) => a.textContent)).toEqual(['D', 'E', 'F', 'G'].map((n): unknown => expect.stringContaining(n)));
    // A crowded day must scroll inside the viewport (jsdom cannot measure it, so the classes are asserted).
    expect(list).toHaveClass('overflow-y-auto');
    expect(list.className).toContain('max-h-');
  });

  it('A_charge_links_to_its_subscription', async () => {
    serve(charges({ charges: [line('2026-11-03', 'Spotify', { price: { amount: '9.99', currency: 'USD' }, converted: eur('9.20') })] }));
    await renderApp('/subscriptions?tab=calendar');

    await waitFor(async () => expect(within(await day('mardi 3 novembre')).getByRole('link', { name: /Spotify/ })).toBeInTheDocument());
    const link = within(await day('mardi 3 novembre')).getByRole('link', { name: /Spotify/ });
    expect(link).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(link).toHaveTextContent('9,20 €');
    // The phone's list of days repeats it, with the day's short name.
    const phone = screen.getByRole('list', { name: 'Prélèvements par jour' });
    expect(within(phone).getByText('mar. 3 nov.')).toBeInTheDocument();
    expect(within(phone).getByRole('link', { name: /Spotify/ })).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
  });

  it('An_empty_month_says_so', async () => {
    serve(charges());
    await renderApp('/subscriptions?tab=calendar');

    expect(await screen.findByText('Aucun prélèvement ce mois-ci')).toBeInTheDocument();
    const figures = (await screen.findByText('Total du mois')).closest('dl')!;
    expect(figures).toHaveTextContent('0,00 €');
    expect(figures).toHaveTextContent('Prélèvements0');
    expect(screen.queryByText('Au-delà de l’horizon de prévision')).not.toBeInTheDocument();
  });

  it('Beyond_the_horizon_says_so', async () => {
    serve(charges({ month: '2031-01', findings: [{ code: 'subscriptions.horizon_reached', message: 'Horizon.', reference: null }] }));
    await renderApp('/subscriptions?tab=calendar&month=2031-01');

    expect(await screen.findByText('Au-delà de l’horizon de prévision')).toBeInTheDocument();
    expect(screen.queryByText('Aucun prélèvement ce mois-ci')).not.toBeInTheDocument();
    expect(screen.getByText('Total du mois').nextElementSibling).toHaveTextContent(/^—$/);
    expect(screen.queryByRole('list', { name: 'Prélèvements par jour' })).not.toBeInTheDocument();
  });

  it('A_month_cut_by_the_horizon_keeps_its_charges_and_says_the_forecast_stops', async () => {
    serve(charges({ month: '2035-10', total: eur('9.99'), charges: [line('2035-10-01', 'Spotify')],
      findings: [{ code: 'subscriptions.horizon_reached', message: 'Stops.', reference: null }] }));
    await renderApp('/subscriptions?tab=calendar&month=2035-10');

    expect(await screen.findByText('Prévision arrêtée en cours de mois')).toBeInTheDocument();
    expect(screen.queryByText('Au-delà de l’horizon de prévision')).not.toBeInTheDocument();
    expect((await screen.findByText('Total du mois')).closest('dl')).toHaveTextContent('9,99 €');
  });

  it('A_failed_month_shows_the_problem_and_keeps_the_month_navigation', async () => {
    server.use(http.get('*/api/v1/subscriptions/charges', () => HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 })));
    await renderApp('/subscriptions?tab=calendar');

    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'novembre 2026' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Mois suivant' })).toBeInTheDocument();
  });

  it('Previous_and_next_change_the_address', async () => {
    const seen = serve(charges());
    const { user, router } = await renderApp('/subscriptions?tab=calendar');

    await user.click(await screen.findByRole('button', { name: 'Mois suivant' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'calendar', month: '2026-12' }));
    await waitFor(() => expect(month(seen)).toBe('2026-12'));
    expect(await screen.findByRole('heading', { name: 'décembre 2026' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Mois précédent' }));
    // Back on the current month, the address drops it.
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('month'));
    await user.click(screen.getByRole('button', { name: 'Mois précédent' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ month: '2026-10' }));
  });
});
