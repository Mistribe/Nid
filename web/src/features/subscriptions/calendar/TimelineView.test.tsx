import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Charges } from '@/api/subscriptions';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { eur } from '../testData';

const SPOTIFY = '0192f0aa-0000-7000-8000-0000000000d1';
type Line = Charges['charges'][number];

export const line = (date: string, displayName: string, over: Partial<Line> = {}): Line =>
  ({ date, itemId: SPOTIFY, displayName, provider: null, price: eur('9.99'), converted: null, isProjected: false, ...over });

/** One answer per month; `byMonth` overrides it, a `null` answers 500. The requests land in `seen` as `scope/month`. */
export function serveMonths(byMonth: Record<string, Partial<Charges> | null> = {}, firstMonth: string | null = '2025-01') {
  const seen: string[] = [];
  server.use(http.get('*/api/v1/subscriptions/charges', ({ request }) => {
    const query = new URL(request.url).searchParams;
    const month = query.get('month')!;
    seen.push(`${query.get('scope')}/${month}`);
    const over = byMonth[month];
    if (over === null) return HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 });
    return HttpResponse.json({ month, total: eur('0.00'), convertedIsEstimate: false, charges: [], findings: [], firstMonth, ...over });
  }));
  return seen;
}

export class FakeObserver {
  static all: FakeObserver[] = [];
  targets: Element[] = [];
  constructor(private readonly callback: IntersectionObserverCallback) { FakeObserver.all.push(this); }
  observe(target: Element) { this.targets.push(target); }
  unobserve() {}
  disconnect() { this.targets = []; }
  takeRecords() { return []; }
  /** Says `target` came into view, as the browser would once scrolled near it. */
  static trigger(target: Element) {
    for (const o of FakeObserver.all.filter((x) => x.targets.includes(target)))
      o.callback([{ target, isIntersecting: true } as IntersectionObserverEntry], o as unknown as IntersectionObserver);
  }
}

const band = () => screen.findByRole('region', { name: 'Frise des prélèvements' });
const segment = async (name: string) => within(await band()).findByRole('group', { name });

describe('Abonnements › frise', () => {
  beforeAll(async () => { await import('../SubscriptionsPage'); await import('./CalendarTab'); await import('./TimelineView'); }, 30_000);
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 10, 15, 12));
    FakeObserver.all = [];
    vi.stubGlobal('IntersectionObserver', FakeObserver);
  });
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it('The_switch_keeps_the_view_in_the_address', async () => {
    serveMonths();
    const { user, router } = await renderApp('/subscriptions?tab=calendar');

    const views = await screen.findByRole('radiogroup', { name: 'Affichage' });
    await user.click(within(views).getByRole('radio', { name: 'Frise' }));
    await waitFor(() => expect(router.state.location.search).toMatchObject({ tab: 'calendar', view: 'timeline' }));
    expect(await band()).toBeInTheDocument();

    await user.click(within(views).getByRole('radio', { name: 'Calendrier' }));
    await waitFor(() => expect(router.state.location.search).not.toHaveProperty('view'));
    expect(await screen.findByRole('group', { name: 'Calendrier du mois' })).toBeInTheDocument();
  });

  it('It_asks_for_the_months_around_today', async () => {
    const seen = serveMonths();
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    for (const name of ['septembre 2026', 'octobre 2026', 'novembre 2026', 'décembre 2026', 'janvier 2027', 'février 2027'])
      expect(await segment(name)).toBeInTheDocument();
    await waitFor(() => expect(seen).toEqual(expect.arrayContaining(
      ['2026-09', '2026-10', '2026-11', '2026-12', '2027-01', '2027-02'].map((m) => `household/${m}`))));
    expect(seen).not.toContain('household/2026-08');
  });

  it('Paid_and_forecast_charges_are_told_apart_by_name', async () => {
    serveMonths({ '2026-11': { charges: [line('2026-11-03', 'Spotify'), line('2026-11-20', 'Spotify', { isProjected: true })] } });
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    const november = await segment('novembre 2026');
    const paid = await within(november).findByRole('link', { name: /^Spotify, 9,99\s€, prélevé le mar\. 3 nov\.$/ });
    const forecast = within(november).getByRole('link', { name: /^Spotify, 9,99\s€, prévu le ven\. 20 nov\.$/ });
    expect(paid).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(forecast).toHaveAttribute('href', `/subscriptions/${SPOTIFY}`);
    expect(forecast).toHaveClass('border-dashed');
    expect(paid).not.toHaveClass('border-dashed');
  });

  it('More_than_three_on_a_day_show_two_and_plus_n', async () => {
    serveMonths({ '2026-11': { charges: ['D', 'E', 'F', 'G'].map((n) => line('2026-11-10', n)) } });
    const { user } = await renderApp('/subscriptions?tab=calendar&view=timeline');

    const november = await segment('novembre 2026');
    await waitFor(() => expect(within(november).getAllByRole('link')).toHaveLength(2));
    await user.click(within(november).getByRole('button', { name: '+2 autres prélèvements' }));
    const list = await screen.findByRole('dialog', { name: 'mardi 10 novembre' });
    expect(within(list).getAllByRole('link')).toHaveLength(4);
  });

  it('A_segment_shows_the_servers_total_and_count', async () => {
    serveMonths({ '2026-12': { total: eur('42.00'), charges: [line('2026-12-03', 'Spotify', { isProjected: true })] } });
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    const december = await segment('décembre 2026');
    await waitFor(() => expect(december).toHaveTextContent('42,00 € · 1 prélèvement'));
  });

  it('A_failed_month_keeps_the_others', async () => {
    serveMonths({ '2026-12': null, '2026-11': { charges: [line('2026-11-03', 'Spotify')] } });
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    expect(await within(await segment('décembre 2026')).findByRole('alert')).toBeInTheDocument();
    expect(await within(await segment('novembre 2026')).findByRole('link', { name: /Spotify/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Mois suivant' })).toBeInTheDocument();
  });

  it('Today_is_marked', async () => {
    serveMonths();
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    expect(await within(await segment('novembre 2026')).findByText('Aujourd’hui')).toBeInTheDocument();
    expect(within(await segment('octobre 2026')).queryByText('Aujourd’hui')).not.toBeInTheDocument();
  });

  it('It_has_no_axe_violations', async () => {
    serveMonths({ '2026-11': { charges: [line('2026-11-03', 'Spotify'), line('2026-11-20', 'Spotify', { isProjected: true })] } });
    const { container } = await renderApp('/subscriptions?tab=calendar&view=timeline');

    await within(await segment('novembre 2026')).findAllByRole('link');
    await expectNoAxeViolations(container);
  });
});
