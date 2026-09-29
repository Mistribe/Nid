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
];

export const server = setupServer(...handlers);
