import { http, HttpResponse } from 'msw/http';
import { server } from '@/test/server';
import type { Spending } from './api';

/** An invented month of the demo household: two categories, one with two children, and some lines left unclassified. */
export const month: Spending = {
  currency: 'EUR', total: '1234.56', incomplete: false, average: '1100.00', from: '2026-11', to: '2026-11',
  categories: [
    { id: '11111111-1111-1111-1111-111111111111', name: 'Courses', amount: '640.10', average: '600.00', children: [
      { id: '11111111-1111-1111-1111-11111111111a', name: 'Supermarché', amount: '520.10', average: '480.00', children: [] },
      { id: '11111111-1111-1111-1111-11111111111b', name: 'Marché', amount: '120.00', average: null, children: [] },
    ] },
    { id: '22222222-2222-2222-2222-222222222222', name: 'Loisirs', amount: '294.46', average: null, children: [] },
  ],
  uncategorised: { amount: '300.00', average: '250.00' },
};

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
