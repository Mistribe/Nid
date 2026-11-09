import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
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
const scrollBy = vi.fn();
const scrollTo = vi.fn();
// jsdom scrolls no element: the band's scrolling is recorded instead.
beforeEach(() => {
  scrollBy.mockReset(); scrollTo.mockReset();
  HTMLElement.prototype.scrollBy = scrollBy;
  HTMLElement.prototype.scrollTo = scrollTo;
});
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

  it('A_failed_current_month_still_shows_the_band', async () => {
    serveMonths({ '2026-11': null, '2026-12': { charges: [line('2026-12-03', 'Spotify', { isProjected: true })] } });
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    expect(await within(await segment('novembre 2026')).findByRole('alert', {}, { timeout: 5000 })).toBeInTheDocument();
    expect(await within(await segment('décembre 2026')).findByRole('link', { name: /Spotify/ })).toBeInTheDocument();
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

describe('Abonnements › frise › défilement', () => {
  beforeAll(async () => { await import('../SubscriptionsPage'); await import('./CalendarTab'); await import('./TimelineView'); }, 30_000);
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 10, 15, 12));
    FakeObserver.all = [];
    vi.stubGlobal('IntersectionObserver', FakeObserver);
  });
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it('The_left_edge_loads_earlier_months_down_to_the_first_charge', async () => {
    const seen = serveMonths({}, '2026-07');
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    await segment('septembre 2026');
    act(() => FakeObserver.trigger(document.querySelector('[data-edge="past"]')!));
    expect(await segment('juillet 2026')).toBeInTheDocument();
    expect(await segment('août 2026')).toBeInTheDocument();
    expect(await within(await band()).findByText('Début de l’historique')).toBeInTheDocument();
    await waitFor(() => expect(seen).toContain('household/2026-07'));
    expect(seen).not.toContain('household/2026-06');
  });

  it('The_right_edge_stops_twelve_months_ahead', async () => {
    const seen = serveMonths();
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    await segment('février 2027');
    for (let i = 0; i < 5; i++) {
      const sentinel = document.querySelector('[data-edge="future"]');
      if (sentinel) act(() => FakeObserver.trigger(sentinel));
    }
    expect(await segment('novembre 2027')).toBeInTheDocument();
    expect(within(await band()).getByText('Fin de la prévision affichée')).toBeInTheDocument();
    expect(document.querySelector('[data-edge="future"]')).toBeNull();
    expect(seen).not.toContain('household/2027-12');
  });

  it('Without_any_charge_the_band_starts_this_month', async () => {
    const seen = serveMonths({}, null);
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    expect(await within(await band()).findByText('Début de l’historique')).toBeInTheDocument();
    expect(document.querySelector('[data-edge="past"]')).toBeNull();
    expect(seen.filter((m) => m < 'household/2026-11')).toEqual([]);
  });

  it('Prepending_keeps_the_view_in_place', async () => {
    serveMonths({}, '2025-01');
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    const region = await band();
    await segment('septembre 2026');
    let width = 1000;
    let left = 500;
    Object.defineProperty(region, 'scrollWidth', { configurable: true, get: () => width });
    Object.defineProperty(region, 'scrollLeft', { configurable: true, get: () => left, set: (v: number) => { left = v; } });
    act(() => { FakeObserver.trigger(document.querySelector('[data-edge="past"]')!); width = 1960; });
    await segment('juin 2026');
    expect(left).toBe(1460);
  });

  it('A_drag_does_not_open_a_subscription', async () => {
    serveMonths({ '2026-11': { charges: [line('2026-11-03', 'Spotify')] } });
    const { user, router } = await renderApp('/subscriptions?tab=calendar&view=timeline');

    const disc = await within(await segment('novembre 2026')).findByRole('link', { name: /Spotify/ });
    await user.pointer([
      { keys: '[MouseLeft>]', target: disc, coords: { clientX: 200, clientY: 50 } },
      { target: disc, coords: { clientX: 120, clientY: 50 } },
      { keys: '[/MouseLeft]', target: disc },
    ]);
    expect(router.state.location.pathname).toBe('/subscriptions');

    await user.click(disc);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/subscriptions/${SPOTIFY}`));
  });

  it('Months_added_during_a_drag_do_not_make_the_band_jump', async () => {
    serveMonths({}, '2020-01');
    const { user } = await renderApp('/subscriptions?tab=calendar&view=timeline');

    const region = await band();
    await segment('septembre 2026');
    let width = 1000;
    let left = 500;
    Object.defineProperty(region, 'scrollWidth', { configurable: true, get: () => width });
    Object.defineProperty(region, 'scrollLeft', { configurable: true, get: () => left, set: (v: number) => { left = v; } });
    await user.pointer([
      { keys: '[MouseLeft>]', target: region, coords: { clientX: 200, clientY: 50 } },
      { target: region, coords: { clientX: 220, clientY: 50 } },
    ]);
    expect(left).toBe(480);
    act(() => { FakeObserver.trigger(document.querySelector('[data-edge="past"]')!); width = 1960; });
    await segment('juin 2026');
    expect(left).toBe(1440);
    await user.pointer({ target: region, coords: { clientX: 230, clientY: 50 } });
    expect(left).toBe(1430); // ten more pixels from where the band now is, not from where the drag began
    await user.pointer({ keys: '[/MouseLeft]', target: region });
  });

  it('Moving_the_mouse_with_no_button_held_does_not_scroll', async () => {
    serveMonths();
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    const region = await band();
    await segment('novembre 2026');
    let left = 500;
    Object.defineProperty(region, 'scrollLeft', { configurable: true, get: () => left, set: (v: number) => { left = v; } });
    // A press on the scrollbar: the browser may never send its pointerup to the page.
    fireEvent.pointerDown(region, { pointerType: 'mouse', button: 0, buttons: 1, clientX: 200 });
    fireEvent.pointerMove(region, { pointerType: 'mouse', buttons: 0, clientX: 100 });
    fireEvent.pointerMove(region, { pointerType: 'mouse', buttons: 0, clientX: 50 });
    expect(left).toBe(500);
  });

  it('A_short_february_has_no_29_tick', async () => {
    serveMonths();
    await renderApp('/subscriptions?tab=calendar&view=timeline');

    const february = await segment('février 2027');
    await within(february).findByText('22');
    expect(within(february).queryByText('29')).not.toBeInTheDocument();
    expect(within(await segment('janvier 2027')).getByText('29')).toBeInTheDocument();
  });

  it('The_buttons_scroll_by_a_month_and_back_to_today', async () => {
    serveMonths();
    const { user } = await renderApp('/subscriptions?tab=calendar&view=timeline');

    await segment('novembre 2026');
    await waitFor(() => expect(scrollTo).toHaveBeenCalled()); // centred on today when it opens
    await user.click(screen.getByRole('button', { name: 'Mois suivant' }));
    expect(scrollBy).toHaveBeenLastCalledWith(expect.objectContaining({ left: expect.any(Number) as number }));
    expect((scrollBy.mock.lastCall![0] as ScrollToOptions).left).toBeGreaterThan(0);
    await user.click(screen.getByRole('button', { name: 'Mois précédent' }));
    expect((scrollBy.mock.lastCall![0] as ScrollToOptions).left).toBeLessThan(0);
    scrollTo.mockClear();
    await user.click(screen.getByRole('button', { name: 'Aujourd’hui' }));
    expect(scrollTo).toHaveBeenCalledTimes(1);
  });

  it('Changing_scope_resets_the_band', async () => {
    const seen = serveMonths();
    const { router } = await renderApp('/subscriptions?tab=calendar&view=timeline');

    await segment('septembre 2026');
    act(() => FakeObserver.trigger(document.querySelector('[data-edge="past"]')!));
    await segment('juin 2026');
    await act(() => router.navigate({ to: '.', search: (prev) => ({ ...prev, scope: 'me' }) }));
    await waitFor(() => expect(seen).toContain('me/2026-11'));
    expect(await segment('septembre 2026')).toBeInTheDocument();
    expect(within(await band()).queryByRole('group', { name: 'juin 2026' })).not.toBeInTheDocument();
  });
});
