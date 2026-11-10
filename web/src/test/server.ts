import { http, HttpResponse } from 'msw/http';
import { setupServer } from 'msw/node';
import { emptyDashboard, emptyMonthChanges, emptyProjection, netWorth } from '@/features/dashboard/testData';
import { overview, sourceOverview } from '@/features/income/testData';
import { holdings as investmentsHoldings, overview as investmentsOverview, performance as investmentsPerformance } from '@/features/investments/testData';
import { overview as loansOverview } from '@/features/loans/testData';
import { propertiesCosts, propertiesOverview, propertiesRental, propertiesValuation } from '@/features/properties/testData';
import { estateRoutine, routine } from '@/features/routine/testData';
import { overview as subscriptionsOverview } from '@/features/subscriptions/testData';
import { aliceSessionResponse, allCapabilities } from './fixtures';

export const handlers = [
  http.get('*/auth/session', () => HttpResponse.json(aliceSessionResponse)),
  http.get('*/api/v1/platform/capabilities', () => HttpResponse.json(allCapabilities)),
  // The shell's token banner reads the list (4A).
  // Every list row names its owners from the household's persons.
  http.get('*/api/v1/core/persons', () => HttpResponse.json([])),
  // The series explorer asks for the points of the keys of its address; none by default.
  http.get('*/api/v1/series', () => HttpResponse.json({ items: [], total: 0, page: 1 })),
  http.get('*/api/v1/series/points', () => HttpResponse.json({ series: [], baseDate: null })),
  // The tax page's year selector reads the past notices.
  http.get('*/api/v1/tax/assessments', () => HttpResponse.json({ items: [] })),
  // ... and its « Évolution » what changed since the year before (#410): nothing to compare.
  http.get('*/api/v1/tax/changes', () => HttpResponse.json({ year: 2026, taxBefore: null, taxBeforeFromNotice: false, taxNow: null, remainder: null, lines: [] })),
  http.get('*/api/v1/core/me/tokens', () => HttpResponse.json([])),
  // A detail page's badges and `⋯` read the item's sharing (#294): shared, view only, owned by Alice.
  http.get('*/api/v1/core/items/:id/sharing', () => HttpResponse.json({ visibility: 'shared', shareLevel: 'view', canChange: true, isOwner: true })),
  // The shell's Foyer / Moi selector shows only to an owner of a private item (issue 85).
  http.get('*/api/v1/core/me/private-items', () => HttpResponse.json({ owns: true })),
  // The shell's badge reads the pending proposals (4C2).
  http.get('*/api/v1/core/proposals', () => HttpResponse.json({ items: [] })),
  // The shell's bell and banner read the notification counts.
  http.get('*/api/v1/platform/notifications/count', () => HttpResponse.json({ unread: 0, criticalUnread: 0 })),
  http.get('*/api/v1/platform/imports', () => HttpResponse.json([])),
  // The settings tabs, the envelope pages and the routine ask whether bank sync is on: off unless a test turns it on.
  http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: false, configured: false })),
  // ... and, once a test turns it on, which bank accounts the person reads (none).
  http.get('*/api/v1/banking/accounts', () => HttpResponse.json([])),
  // ... and the rules they kept (none).
  http.get('*/api/v1/banking/rules', () => HttpResponse.json([])),
  http.get('*/api/v1/dashboard', () => HttpResponse.json(emptyDashboard)),
  // The dashboard's notice, the documents page and an item's documents tab read the expected documents (#414): none.
  http.get('*/api/v1/documents/expected', () => HttpResponse.json({ items: [] })),
  // The home page's goals card reads the open goals (none: it stays absent).
  http.get('*/api/v1/goals', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/dashboard/net-worth', () => HttpResponse.json(netWorth([]))),
  // ... and « Depuis le mois dernier », hidden in the first month.
  http.get('*/api/v1/dashboard/changes', () => HttpResponse.json(emptyMonthChanges)),
  // ... and the projection card below it, hidden while the household has nothing.
  http.get('*/api/v1/projection', () => HttpResponse.json(emptyProjection())),
  // The annual review (#12): nothing to review yet.
  http.get('*/api/v1/reports/year', () => HttpResponse.json({
    year: 2026, start: '2025-12-31', end: '2026-11-10', toDate: true, years: [2026], netWorth: null, investments: null, income: null, findings: [],
  })),
  // The subscriptions list is read by every test that lands on it.
  http.get('*/api/v1/subscriptions', () => HttpResponse.json({
    items: [], householdCashCost: [], householdAmortizedCost: [], householdCashCostConverted: null,
    householdAmortizedCostConverted: null, householdConvertedIsEstimate: false, sharedPurchaseIds: [], groups: [], findings: [],
  })),
  // ... its overview, the tab it opens on (an empty household) ...
  http.get('*/api/v1/subscriptions/overview', () => HttpResponse.json(subscriptionsOverview())),
  // ... and the labels its filter offers.
  http.get('*/api/v1/subscriptions/labels', () => HttpResponse.json([])),
  http.get('*/api/v1/investments/envelopes', () => HttpResponse.json({ items: [] })),
  // ... and their overview, the tab the page opens on (no envelope).
  http.get('*/api/v1/investments/overview', () => HttpResponse.json(investmentsOverview())),
  http.get('*/api/v1/investments/overview/performance', () => HttpResponse.json(investmentsPerformance())),
  http.get('*/api/v1/investments/overview/holdings', () => HttpResponse.json(investmentsHoldings())),
  http.get('*/api/v1/investments/routine', () => HttpResponse.json(routine([]))),
  http.get('*/api/v1/realestate/routine', () => HttpResponse.json(estateRoutine())),
  http.get('*/api/v1/income/overview', () => HttpResponse.json(overview())),
  http.get('*/api/v1/income/sources', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/realestate/loans', () => HttpResponse.json({ items: [] })),
  // ... and their overview, the tab the page opens on (no loan).
  http.get('*/api/v1/realestate/loans/overview', () => HttpResponse.json(loansOverview())),
  http.get('*/api/v1/realestate/properties', () => HttpResponse.json({ items: [] })),
  // ... and their overview, the tab the page opens on (no property).
  http.get('*/api/v1/realestate/properties/overview', () => HttpResponse.json(propertiesOverview())),
  http.get('*/api/v1/realestate/properties/overview/rental', () => HttpResponse.json(propertiesRental())),
  http.get('*/api/v1/realestate/properties/overview/costs', () => HttpResponse.json(propertiesCosts())),
  http.get('*/api/v1/realestate/properties/overview/valuation', () => HttpResponse.json(propertiesValuation())),
  // Every income source page reads its overview (figures, evolution, timeline).
  http.get('*/api/v1/income/sources/:id/overview', () => HttpResponse.json(sourceOverview())),
  // ... and the history tab reads the documents anchored on it (the payslips).
  http.get('*/api/v1/documents', () => HttpResponse.json({ items: [] })),
  http.get('*/api/v1/income/sources/:id/expected', ({ params, request }) => {
    const url = new URL(request.url);
    return HttpResponse.json({ id: params.id, from: url.searchParams.get('from'), to: url.searchParams.get('to'), months: [] });
  }),
  // Every property and envelope page asks the tax if sold today; no item here: not found, the section stays hidden.
  http.get('*/api/v1/tax/sale', () => HttpResponse.json({ status: 404, title: 'Not found' }, { status: 404 })),
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
