import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DashboardEvent } from '@/api/dashboard';
import type { CalendarAmounts, CalendarCharge } from './amounts';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { event } from '../dashboard/testData';

function serve(events: DashboardEvent[] = [], amounts: CalendarAmounts | null = null) {
  const seen: URLSearchParams[] = [];
  server.use(http.get('*/api/v1/calendar', ({ request }) => {
    const query = new URL(request.url).searchParams;
    seen.push(query);
    return HttpResponse.json({ from: query.get('from'), to: query.get('to'), events, amounts: query.get('amounts') === 'true' ? amounts : null });
  }));
  return seen;
}

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const total = (amount: string | null, missingCurrency: string | null = null) => ({ perCurrency: [], total: amount ? eur(amount) : null, missingCurrency });
function charge(over: Partial<CalendarCharge>): CalendarCharge {
  return { on: '2026-11-15', module: 'loans', id: '00000000-0000-7000-8000-0000000000e1', name: 'Prêt maison', kind: 'loan_instalment',
    amount: eur('1200.00'), converted: eur('1200.00'), missingCurrency: null, credit: false, ...over };
}

/** Invented figures: a loan's instalment on the 15th, two subscriptions and a rent on the 1st, a property's tax on the 20th. */
const NOVEMBER: CalendarAmounts = {
  currency: 'EUR', out: total('2450.00'), in: total('800.00'), balance: total('-1650.00'),
  days: [
    { on: '2026-11-01', out: total('50.00'), in: total('800.00'), heavy: true },
    { on: '2026-11-15', out: total('1200.00'), in: null, heavy: true },
    { on: '2026-11-20', out: total(null, 'USD'), in: null, heavy: false },
  ],
  charges: [
    charge({ on: '2026-11-01', module: 'subscriptions', id: '00000000-0000-7000-8000-0000000000e2', name: 'Box internet', kind: 'subscription', amount: eur('30.00') }),
    charge({ on: '2026-11-01', module: 'subscriptions', id: '00000000-0000-7000-8000-0000000000e3', name: 'Musique', kind: 'subscription', amount: eur('20.00') }),
    charge({ on: '2026-11-01', module: 'properties', id: '00000000-0000-7000-8000-0000000000e4', name: 'Studio', kind: 'rent', amount: eur('800.00'), credit: true }),
    charge({}),
    charge({ on: '2026-11-20', module: 'properties', id: '00000000-0000-7000-8000-0000000000e4', name: 'Studio', kind: 'property_cost',
      amount: { amount: '1200.00', currency: 'USD' }, converted: null, missingCurrency: 'USD' }),
  ],
};
const grid = () => screen.findByRole('group', { name: 'Calendrier du mois' });

function serveTimeline(events: DashboardEvent[] = []) {
  const seen: URLSearchParams[] = [];
  server.use(http.get('*/api/v1/calendar/timeline', ({ request }) => {
    const query = new URL(request.url).searchParams;
    seen.push(query);
    return HttpResponse.json({ from: '2026-11-15', to: `${2026 + Number(query.get('horizon'))}-11-15`, events });
  }));
  return seen;
}

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
    // Childcare (#241, amounts only) keeps its choice in the address, never in the request without amounts.
    await waitFor(() => expect(router.state.location.search).toEqual({ scope: 'me', month: '2026-12', modules: 'loans,properties,investments,income,tax,childcare' }));
    await waitFor(() => expect(seen.at(-1)?.get('modules')).toBe('loans,properties,investments,income,tax'));
    expect(screen.getByRole('button', { name: 'Mois précédent' })).toBeEnabled();
  });

  it('keeps the last module checked', async () => {
    serve();
    await renderApp('/calendar?modules=tax,unknown');

    expect(await screen.findByRole('checkbox', { name: 'Impôts' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Abonnements' })).not.toBeChecked();
  });

  it('switches to the timeline: the milestones over the horizon, one row per module, each mark linked to its item', async () => {
    serve();
    const seen = serveTimeline([
      event({ on: '2028-03-01', kind: 'tax_milestone', module: 'investments', id: '00000000-0000-7000-8000-0000000000d1', name: 'PEA Alice', label: 'pea_5y', amount: null }),
      event({ on: '2031-04-15', kind: 'loan_end', module: 'loans', id: '00000000-0000-7000-8000-0000000000d2', name: 'Prêt maison', label: null, amount: null }),
      event({ on: '2031-05-01', kind: 'tenancy_end', module: 'loans', id: '00000000-0000-7000-8000-0000000000d2', name: 'Prêt maison', label: 'T2', amount: null }),
    ]);
    const { user, router, container } = await renderApp('/calendar?modules=loans,investments');

    await user.click(await screen.findByRole('radio', { name: 'Frise' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ modules: 'loans,investments', view: 'timeline' }));
    await waitFor(() => expect(seen.at(-1)?.toString()).toBe('scope=household&horizon=5&modules=loans%2Cinvestments'));
    expect(screen.queryByRole('button', { name: 'Mois suivant' })).not.toBeInTheDocument();

    const band = await screen.findByRole('region', { name: 'Frise des échéances du 15 novembre 2026 au 15 novembre 2031' });
    // The checked modules only, in the filters' order.
    expect(within(band).getAllByRole('list').map((l) => l.getAttribute('aria-labelledby') && document.getElementById(l.getAttribute('aria-labelledby')!)?.textContent))
      .toEqual(['Crédits', 'Placements']);
    const pea = within(within(band).getByRole('list', { name: 'Placements' })).getByRole('link');
    expect(pea).toHaveTextContent('5 ans du PEA : la fiscalité des retraits change · PEA Alice · 1 mars 2028');
    expect(pea).toHaveAttribute('href', '/investments/00000000-0000-7000-8000-0000000000d1');
    // Two weeks apart on five years: one mark, opening both.
    await user.click(within(band).getByRole('button', { name: '2 échéances, du 15 avril 2031 au 1 mai 2031' }));
    expect(await screen.findByRole('dialog', { name: '2 échéances, du 15 avril 2031 au 1 mai 2031' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('radio', { name: '10 ans' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ modules: 'loans,investments', view: 'timeline', horizon: 10 }));
    await waitFor(() => expect(seen.at(-1)?.get('horizon')).toBe('10'));
  });

  it('reads the timeline view and its horizon from the address', async () => {
    const seen = serveTimeline();
    await renderApp('/calendar?view=timeline&horizon=1');

    expect(await screen.findByRole('radio', { name: '1 an' })).toBeChecked();
    await waitFor(() => expect(seen.at(-1)?.get('horizon')).toBe('1'));
    expect(await screen.findByText('Aucune échéance ponctuelle sur cet horizon pour ces modules.')).toBeInTheDocument();
  });

  it('shows the money of the month with its amounts on: the totals, each day out and in, the heavy days and every charge', async () => {
    const seen = serve([event({ on: '2026-11-03', name: 'Amazon Prime' })], NOVEMBER);
    const { user, router, container } = await renderApp('/calendar');

    // Off: neither a total nor childcare, and the request is #239's.
    await waitFor(() => expect(seen.at(-1)?.toString()).toBe('scope=household&from=2026-11-01&to=2026-11-30'));
    expect(screen.queryByText('Sorties prévues')).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: 'Garde' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('switch', { name: 'Montants' }));
    await waitFor(() => expect(router.state.location.search).toEqual({ amounts: 1 }));
    await waitFor(() => expect(seen.at(-1)?.get('amounts')).toBe('true'));
    expect(seen.at(-1)?.has('modules')).toBe(false);
    expect(await screen.findByRole('checkbox', { name: 'Garde' })).toBeChecked();

    const header = (await screen.findByText('Sorties prévues')).closest('dl')!;
    expect(header).toHaveTextContent(/Sorties prévues2\s450,00\s€/);
    expect(header).toHaveTextContent(/Entrées prévues800,00\s€/);
    expect(header).toHaveTextContent(/Solde du mois.*-1\s650,00\s€/);

    // The cells: the day's totals, the heavy days marked; a missing rate reads « Inconnu ».
    const first = within(await grid()).getByRole('group', { name: 'dimanche 1 novembre' });
    expect(first).toHaveTextContent(/Jour chargé, Sorties −50,00\s€/);
    expect(first).toHaveTextContent(/Entrées \+800,00\s€/);
    expect(within(await grid()).getByRole('group', { name: 'vendredi 20 novembre' })).toHaveTextContent('Sorties −Inconnu');
    expect(within(await grid()).getByRole('group', { name: 'vendredi 20 novembre' })).not.toHaveTextContent('Jour chargé');
    const fifteenth = within(await grid()).getByRole('group', { name: 'dimanche 15 novembre' });
    expect(within(fifteenth).getByRole('link', { name: 'Prêt maison' })).toHaveAttribute('href', '/loans/00000000-0000-7000-8000-0000000000e1');

    // The phone's list: every charge in full, with its amount in its own currency.
    const list = screen.getByRole('list', { name: 'Échéances jour par jour' });
    expect(within(list).getByRole('link', { name: /^Loyer.*Studio/ })).toHaveTextContent(/\+800,00\s€/);
    expect(within(list).getByRole('link', { name: /^Charge du bien.*Studio/ })).toHaveTextContent(/−1\s200,00\s\$US/);
    expect(within(list).getAllByRole('link')).toHaveLength(6);
    await expectNoAxeViolations(container);

    // Childcare is a module like the others then.
    await user.click(screen.getByRole('checkbox', { name: 'Garde' }));
    await waitFor(() => expect(seen.at(-1)?.get('modules')).toBe('subscriptions,loans,properties,investments,income,tax'));
  });
});
