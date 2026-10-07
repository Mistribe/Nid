import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import { aliceSessionResponse, allCapabilities } from './fixtures';

export const handlers = [
  http.get('*/auth/session', () => HttpResponse.json(aliceSessionResponse)),
  http.get('*/api/v1/platform/capabilities', () => HttpResponse.json(allCapabilities)),
  http.get('*/api/v1/platform/imports', () => HttpResponse.json([])),
  // The home page is the subscriptions list (router.homePath): every test that lands there reads it.
  http.get('*/api/v1/subscriptions', () => HttpResponse.json({
    items: [], householdCashCost: [], householdAmortizedCost: [], householdCashCostConverted: null,
    householdAmortizedCostConverted: null, householdConvertedIsEstimate: false, sharedPurchaseIds: [], findings: [],
  })),
  http.get('*/api/v1/investments/envelopes', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/income/sources', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/income/sources/:id/expected', ({ params, request }) => {
    const url = new URL(request.url);
    return HttpResponse.json({ id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'), months: [] });
  }),
  // Every envelope detail also reads its value series; an empty one says « la courbe apparaît à partir de deux valeurs ».
  http.get('*/api/v1/investments/envelopes/:id/series', ({ params }) => HttpResponse.json({ id: params.id, points: [] })),
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
