import { http, HttpResponse } from 'msw/http';
import { server } from '@/test/server';
import type { Spending } from './api';

/** An invented month of the demo household: two categories, one with two children, and some lines left unclassified. */
export const month: Spending = {
  currency: 'EUR', total: '1234.56', incomplete: false, budgetIncomplete: false, average: '1100.00', from: '2026-11', to: '2026-11', unattachedCards: [],
  categories: [
    { id: '11111111-1111-1111-1111-111111111111', name: 'Courses', amount: '640.10', average: '600.00', children: [
      { id: '11111111-1111-1111-1111-11111111111a', name: 'Supermarché', amount: '520.10', average: '480.00', children: [] },
      { id: '11111111-1111-1111-1111-11111111111b', name: 'Marché', amount: '120.00', average: null, children: [] },
    ] },
    { id: '22222222-2222-2222-2222-222222222222', name: 'Loisirs', amount: '294.46', average: null, children: [
      { id: '22222222-2222-2222-2222-22222222222a', name: 'Cinéma', amount: '44.00', average: null, children: [] },
    ] },
  ],
  uncategorised: { amount: '300.00', average: '250.00', count: 12 },
  months: [{ month: '2026-11', total: '1234.56', income: '3200.00', committed: '1500.00', everyday: '900.00', left: '800.00' }],
  // #425: Accueil's month, and how it would end as usual.
  income: '3200.00', committed: '1500.00', everyday: '900.00', left: '800.00', leftEstimate: '600.00', savingsRate: 25,
  accounts: [],
};

/**
 * The same household over the 12 months to November 2026, each month's figure the server's: a raise in June (3 000, then 3 200), and
 * a December that spent more than came in.
 */
export const year: Spending = {
  ...month, total: '14400.00', from: '2025-12', to: '2026-11',
  income: '37200.00', committed: '18000.00', everyday: '10950.00', left: '8250.00', leftEstimate: null, savingsRate: 22.2,
  months: ['2025-12', '2026-01', '2026-02', '2026-03', '2026-04', '2026-05', '2026-06', '2026-07', '2026-08', '2026-09', '2026-10', '2026-11']
    .map((m, i) => ({
      month: m, total: i === 0 ? '1500.00' : '1172.73', income: i < 6 ? '3000.00' : '3200.00', committed: '1500.00',
      everyday: i === 0 ? '1600.00' : '850.00', left: i === 0 ? '-100.00' : i < 6 ? '650.00' : '850.00',
    })),
};

/** The same month in Foyer with budgets: Courses on a yearly budget and over it, Loisirs filled by its commitments and ahead of the month. */
export const budgeted: Spending = {
  ...month,
  categories: [
    { ...month.categories[0]!, budget: { planned: '600.00', committed: null, source: 'typed', period: 'year', left: '-40.10', expectedSoFar: '380.00', state: 'over' } },
    { ...month.categories[1]!, budget: { planned: '300.00', committed: '300.00', source: 'committed', period: 'month', left: '5.54', expectedSoFar: '190.00', state: 'ahead' } },
  ],
};

/** The household's budgets and their writes, each `[method, categoryId, body]`. */
export function budgetServer(budgets = [{ categoryId: budgeted.categories[0]!.id, categoryName: 'Courses', amount: '7200.00', period: 'year' }]) {
  const writes: [string, string, unknown][] = [];
  server.use(
    http.get('*/api/v1/banking/budgets', () => HttpResponse.json(budgets)),
    http.put('*/api/v1/banking/budgets/:id', async ({ request, params }) => {
      const body = await request.json();
      writes.push(['PUT', params.id as string, body]);
      return HttpResponse.json({ categoryId: params.id, categoryName: 'x', ...(body as object) });
    }),
    http.delete('*/api/v1/banking/budgets/:id', ({ params }) => { writes.push(['DELETE', params.id as string, null]); return new HttpResponse(null, { status: 204 }); }),
  );
  return writes;
}

/** Bank sync on, one account read, and the spending answer; the requests' query strings land in `asked`. */
export function spendingServer(view: Spending = month) {
  const asked: string[] = [];
  server.use(
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: true })),
    http.get('*/api/v1/banking/accounts', () => HttpResponse.json([{ id: 'a1', name: 'Compte joint' }])),
    http.get('*/api/v1/banking/spending', ({ request }) => { asked.push(new URL(request.url).search); return HttpResponse.json(view); }),
  );
  return asked;
}
