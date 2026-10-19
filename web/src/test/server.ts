import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { emptyDashboard, netWorth } from '@/features/dashboard/testData';
import { estateRoutine, routine } from '@/features/routine/testData';
import { aliceSessionResponse, allCapabilities } from './fixtures';

export const handlers = [
  http.get('*/auth/session', () => HttpResponse.json(aliceSessionResponse)),
  http.get('*/api/v1/platform/capabilities', () => HttpResponse.json(allCapabilities)),
  // The shell's token banner reads the list (4A).
  // Every list row names its owners from the household's persons.
  http.get('*/api/v1/core/persons', () => HttpResponse.json([])),
  http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([])),
  // The shell's badge reads the pending proposals (4C2).
  http.get('*/api/v1/core/proposals', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/platform/imports', () => HttpResponse.json([])),
  http.get('*/api/v1/dashboard', () => HttpResponse.json(emptyDashboard)),
  http.get('*/api/v1/dashboard/net-worth', () => HttpResponse.json(netWorth([]))),
  // The subscriptions list is read by every test that lands on it.
  http.get('*/api/v1/subscriptions', () => HttpResponse.json({
    items: [], householdCashCost: [], householdAmortizedCost: [], householdCashCostConverted: null,
    householdAmortizedCostConverted: null, householdConvertedIsEstimate: false, sharedPurchaseIds: [], findings: [],
  })),
  http.get('*/api/v1/investments/envelopes', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine([]))),
  http.get('*/api/v1/realestate/routine', () => HttpResponse.json(estateRoutine())),
  http.get('*/api/v1/income/sources', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/realestate/loans', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/realestate/properties', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/income/sources/:id/expected', ({ params, request }) => {
    const url = new URL(request.url);
    return HttpResponse.json({ id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'), months: [] });
  }),
  // Every envelope detail also reads its value series; an empty one says « la courbe apparaît à partir de deux valeurs ».
  http.get('*/api/v1/investments/envelopes/:id/series', ({ params }) => HttpResponse.json({ id: params.id, points: [] })),
  // ... its holdings (none: the « Supports » tab invites to detail them) ...
  http.get('*/api/v1/investments/envelopes/:id/holdings', ({ params }) => HttpResponse.json({ envelopeId: params.id, currency: 'EUR', holdings: [], warnings: [] })),
  // ... and the performance of the default period; a period with no figures says so rather than showing zeros.
  http.get('*/api/v1/investments/envelopes/:id/performance', ({ request }) => {
    const url = new URL(request.url);
    return HttpResponse.json({
      from: url.searchParams.get('from'), to: url.searchParams.get('to'),
      startAsOf: null, startValue: null, endAsOf: null, endValue: null, netContributions: null, performance: null,
    });
  }),
];

export const server = setupServer(...handlers);
