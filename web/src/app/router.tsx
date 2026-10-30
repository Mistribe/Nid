import {
  createRootRouteWithContext,
  createRoute,
  createRouter,
  lazyRouteComponent,
  redirect,
  retainSearchParams,
  RouterProvider,
  type RouterHistory,
} from '@tanstack/react-router';
import { useState } from 'react';
import { DashboardPage } from '@/features/dashboard/DashboardPage';
import type { Panel } from '@/features/dashboard/KeyFigures';
import { ForbiddenPage } from './ForbiddenPage';
import { NotFoundPage } from './NotFoundPage';
import { useSession, type Capabilities } from './Providers';
import { Shell } from './Shell';

const PANELS: readonly Panel[] = ['net_worth', 'income', 'commitments', 'margin'];

export type RouterContext = { capabilities: Capabilities };

/** Foyer is the default and stays out of the URL; Moi is `?scope=me` (screens.md §1). */
export type RootSearch = { scope?: 'me' };
/** The tax page's tab and the year of its figures (absent: the tab's own default). */
export type TaxSearch = RootSearch & DetailSearch & { year?: string };
export type RoutineSearch = { on?: string };
export type BanksSearch = { connected?: string; error?: string };
/** The home page: the curve's span, a figure panel, the projection's horizon (`10y` stays out of the URL) and its reading (`0`: current value). */
export type DashboardSearch = { curve?: '3y' | 'all'; panel?: Panel; projection?: ProjectionHorizon; real?: 0 };
export type ProjectionHorizon = '5y' | '20y' | '30y' | `retire:${string}`;
/** The kind of scenario to create: a purchase without it (the page it always was), a projection with `projection`. */
export type NewScenarioSearch = { kind?: 'projection' };
const RETIRE = /^retire:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * The list's own filters, in the URL beside the root scope (labels comma-joined, 1E2b2 design §3.3), and its tab. The
 * defaults stay out of the URL: `status` active, `group` provider (subscriptions dashboard design §3.3), `month` (the
 * calendar's, `yyyy-mm`) the current one.
 */
export type SubscriptionsSearch = {
  status?: 'active' | 'ended' | 'all'; q?: string; label?: string; group?: 'provider' | 'label' | 'person' | 'none'; tab?: string;
  month?: string;
};

/** The envelopes list's own filter: closed envelopes shown on demand (2B2b design); anything but `1` is dropped. */
/**
 * The envelopes' list filter (closed ones on demand), the tab (investments dashboard §3.1), the curve's span, named as the
 * dashboard's (`12m` is the default and stays out of the URL), and the performance's period (§4.3: both dates or neither).
 */
export type InvestmentsSearch = { closed?: 1; tab?: string; curve?: '3y' | 'all'; from?: string; to?: string };

/**
 * The income page's own filters: ended sources shown on demand (2C2a design §4), the overview period (`year` is the
 * default and stays out of the URL), person, kind and isolated source; anything invalid is dropped.
 */
export type IncomeSearch = { ended?: 1; period?: '12m' | '3y'; person?: string; kind?: string; source?: string };

/**
 * The income detail's own choices: the history period (2C2b design §5, `last12` by default), the one-offs listed (issue
 * #22) and the evolution chart's period (income dashboard §4, `12m` by default); the defaults stay out of the URL.
 */
export type IncomeDetailSearch = { expected?: 'next12' | 'year' | 'next_year'; oneOffs?: 'all'; period?: '3y' | 'all'; tab?: string };

/**
 * The properties page's tab (properties dashboard §3.1) and its curve's span, `history` (`curve` is the other pages' with another
 * default): `3y` is the default and stays out of the URL; the rental tab's property shown alone (§4.3).
 */
export type PropertiesSearch = { tab?: string; history?: '12m' | 'all'; property?: string };

/** The detail screens' tab (`useDetailTab` validates it against its own ids); the first tab stays out of the URL. */
export type DetailSearch = { tab?: string };
const isIsoDate = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);

// The router parses a numeric value (`tab=2044`) to a number: both spellings are read.
export const detailSearch = (search: Record<string, unknown>): DetailSearch =>
  ((typeof search.tab === 'string' && search.tab !== '') || typeof search.tab === 'number' ? { tab: String(search.tab) } : {});

/** The loans list's own filter: ended loans shown on demand (3B1 design §5), anything but `1` dropped; the tab (loans dashboard §3.1); the timeline's window (§4.2). */
export type LoansSearch = { ended?: 1; tab?: string; span?: '5y' | '10y' };

/** Issue #17: a new loan opened from a new property, already linked to it. */
export type NewLoanSearch = { property?: string };
export type ReceiptSearch = { month?: string };
/** The arrears letter's payment deadline, in days from today: 1 to 60, anything else dropped (default 15 on the page). */
export type ArrearsLetterSearch = { days?: number };
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** 4C2: the proposals' tab; pending is the default and is dropped from the URL. */
export type ProposalsSearch = { status?: 'approved' | 'rejected' };

export function homePath(capabilities: Capabilities): '/dashboard' | '/subscriptions' | '/import' | '/settings/appearance' {
  if (capabilities.modules.includes('core')) return '/dashboard';
  if (capabilities.modules.includes('subscriptions')) return '/subscriptions';
  if (capabilities.modules.includes('import')) return '/import';
  return '/settings/appearance';
}

/** A list means « any of ». */
const requireModule = (module: string | string[]) => ({ context }: { context: RouterContext }) => {
  if (![module].flat().some((m) => context.capabilities.modules.includes(m))) redirect({ to: homePath(context.capabilities), throw: true });
};

const rootRoute = createRootRouteWithContext<RouterContext>()({
  validateSearch: (search: Record<string, unknown>): RootSearch => (search.scope === 'me' ? { scope: 'me' } : {}),
  // Foyer / Moi survives every link and « Retour » (spec 2D2a decision 4); the selector writing `scope: undefined` still clears it.
  search: { middlewares: [retainSearchParams<RootSearch>(['scope'])] },
  component: Shell,
  notFoundComponent: NotFoundPage,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  beforeLoad: ({ context }) => { redirect({ to: homePath(context.capabilities), throw: true }); },
});

const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/dashboard',
  beforeLoad: requireModule('core'),
  validateSearch: (search: Record<string, unknown>): DashboardSearch =>
    ({
      ...(search.curve === '3y' || search.curve === 'all' ? { curve: search.curve } : {}),
      ...(PANELS.includes(search.panel as Panel) ? { panel: search.panel as Panel } : {}),
      ...(search.projection === '5y' || search.projection === '20y' || search.projection === '30y' ? { projection: search.projection } : {}),
      ...(typeof search.projection === 'string' && RETIRE.test(search.projection) ? { projection: search.projection as ProjectionHorizon } : {}),
      ...(search.real === 0 || search.real === '0' ? { real: 0 as const } : {}),
    }),
  component: DashboardPage,
});

const subscriptionsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/subscriptions',
  beforeLoad: requireModule('subscriptions'),
  validateSearch: (search: Record<string, unknown>): SubscriptionsSearch => ({
    ...(search.status === 'ended' || search.status === 'all' ? { status: search.status } : {}),
    ...(search.group === 'label' || search.group === 'person' || search.group === 'none' ? { group: search.group } : {}),
    ...(typeof search.q === 'string' && search.q !== '' ? { q: search.q } : {}),
    ...(typeof search.label === 'string' && search.label !== '' ? { label: search.label } : {}),
    // A malformed month would be a 400: it is overwritten (the raw key would survive a mere omission), and the calendar
    // shows the current one.
    month: typeof search.month === 'string' && /^\d{4}-(0[1-9]|1[0-2])$/.test(search.month) ? search.month : undefined,
    ...detailSearch(search),
  }),
  component: lazyRouteComponent(() => import('@/features/subscriptions/SubscriptionsPage'), 'SubscriptionsPage'),
});

const investmentsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/investments',
  beforeLoad: requireModule('investments'),
  validateSearch: (search: Record<string, unknown>): InvestmentsSearch => ({
    ...(search.closed === 1 || search.closed === '1' ? { closed: 1 as const } : {}),
    ...(search.curve === '3y' || search.curve === 'all' ? { curve: search.curve } : {}),
    ...(isIsoDate(search.from) && isIsoDate(search.to) ? { from: search.from, to: search.to } : {}),
    ...detailSearch(search),
  }),
  component: lazyRouteComponent(() => import('@/features/investments/EnvelopesPage'), 'EnvelopesPage'),
});

// Loaded on demand: the wizard and its form libraries stay out of the initial bundle, as for subscriptions.
const investmentNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/investments/new',
  beforeLoad: requireModule('investments'),
  component: lazyRouteComponent(() => import('@/features/investments/NewEnvelope'), 'NewEnvelope'),
});

const investmentDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/investments/$id',
  validateSearch: detailSearch,
  beforeLoad: requireModule('investments'),
  component: lazyRouteComponent(() => import('@/features/investments/EnvelopeDetailPage'), 'EnvelopeDetailPage'),
});

const holdingDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/investments/$id/holdings/$holdingId',
  beforeLoad: requireModule('investments'),
  component: lazyRouteComponent(() => import('@/features/investments/holdings/HoldingPage'), 'HoldingPage'),
});

const goalsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/goals',
  beforeLoad: requireModule('goals'),
  validateSearch: detailSearch,
  component: lazyRouteComponent(() => import('@/features/goals/GoalsPage'), 'GoalsPage'),
});

const goalNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/goals/new',
  beforeLoad: requireModule('goals'),
  component: lazyRouteComponent(() => import('@/features/goals/NewGoal'), 'NewGoal'),
});

const goalDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/goals/$id',
  validateSearch: detailSearch,
  beforeLoad: requireModule('goals'),
  component: lazyRouteComponent(() => import('@/features/goals/GoalDetailPage'), 'GoalDetailPage'),
});

// Documents design §2.2: « Tous », then « À classer » under `?tab=inbox`.
const documentsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/documents',
  beforeLoad: requireModule('documents'),
  validateSearch: detailSearch,
  component: lazyRouteComponent(() => import('@/features/documents/DocumentsPage'), 'DocumentsPage'),
});

const documentDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/documents/$id',
  beforeLoad: requireModule('documents'),
  component: lazyRouteComponent(() => import('@/features/documents/DocumentDetailPage'), 'DocumentDetailPage'),
});

const incomeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/income',
  beforeLoad: requireModule('income'),
  validateSearch: (search: Record<string, unknown>): IncomeSearch => ({
    ...(search.ended === 1 || search.ended === '1' ? { ended: 1 as const } : {}),
    ...(search.period === '12m' || search.period === '3y' ? { period: search.period } : {}),
    ...(typeof search.person === 'string' && search.person ? { person: search.person } : {}),
    ...(typeof search.kind === 'string' && search.kind ? { kind: search.kind } : {}),
    ...(typeof search.source === 'string' && search.source ? { source: search.source } : {}),
  }),
  component: lazyRouteComponent(() => import('@/features/income/IncomeSourcesPage'), 'IncomeSourcesPage'),
});

// Tax estimate spec §9: one page, its tabs in `?tab=`.
const taxRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/tax',
  beforeLoad: requireModule('income'),
  // The router parses `year=2025` to a number: both spellings are read, anything but 4 digits is dropped.
  validateSearch: (search: Record<string, unknown>): TaxSearch => ({
    ...detailSearch(search),
    ...(/^\d{4}$/.test(String(search.year)) ? { year: String(search.year) } : {}),
  }),
  component: lazyRouteComponent(() => import('@/features/tax/TaxPage'), 'TaxPage'),
});

const taxAssessmentNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/tax/assessments/new',
  beforeLoad: requireModule('income'),
  component: lazyRouteComponent(() => import('@/features/tax/assessments/AssessmentForm'), 'NewAssessment'),
});

const taxAssessmentEditRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/tax/assessments/$id/edit',
  beforeLoad: requireModule('income'),
  component: lazyRouteComponent(() => import('@/features/tax/assessments/AssessmentForm'), 'EditAssessment'),
});

// Property purchase scenarios spec §7 and projection spec §8: list, creation in steps (the kind in `?kind=`), detail by kind.
const scenariosRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/scenarios',
  component: lazyRouteComponent(() => import('@/features/scenarios/ScenariosPage'), 'ScenariosPage'),
});

const scenarioNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/scenarios/new',
  validateSearch: (search: Record<string, unknown>): NewScenarioSearch => (search.kind === 'projection' ? { kind: 'projection' } : {}),
  component: lazyRouteComponent(() => import('@/features/scenarios/new/NewScenarioPage'), 'NewScenarioPage'),
});

const projectionScenarioRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/scenarios/projection/$id',
  validateSearch: detailSearch,
  component: lazyRouteComponent(() => import('@/features/scenarios/projection/routes'), 'ProjectionScenarioPage'),
});

const scenarioDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/scenarios/$id',
  validateSearch: detailSearch,
  component: lazyRouteComponent(() => import('@/features/scenarios/ScenarioDetailPage'), 'ScenarioDetailPage'),
});

// Notifications exist for any adult: no module guard. `todo` is the first tab and stays out of the URL.
const notificationsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/notifications',
  validateSearch: (search: Record<string, unknown>): DetailSearch => (search.tab === 'snoozed' || search.tab === 'history' ? { tab: search.tab } : {}),
  component: lazyRouteComponent(() => import('@/features/notifications/NotificationsPage'), 'NotificationsPage'),
});

const operationsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/operations',
  component: lazyRouteComponent(() => import('@/features/operations/UnlinkedPage'), 'UnlinkedPage'),
});

const routineRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/routine',
  beforeLoad: requireModule(['investments', 'realestate']),
  validateSearch: (search: Record<string, unknown>): RoutineSearch =>
    (typeof search.on === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(search.on) ? { on: search.on } : {}),
  component: lazyRouteComponent(() => import('@/features/routine/RoutinePage'), 'RoutinePage'),
});

// Loaded on demand, as the envelope's: the forms and their libraries stay out of the initial bundle.
const incomeNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/income/new',
  beforeLoad: requireModule('income'),
  component: lazyRouteComponent(() => import('@/features/income/NewIncomeSource'), 'NewIncomeSource'),
});

const incomeDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/income/$id',
  beforeLoad: requireModule('income'),
  validateSearch: (search: Record<string, unknown>): IncomeDetailSearch =>
    ({
      ...(search.expected === 'next12' || search.expected === 'year' || search.expected === 'next_year' ? { expected: search.expected } : {}),
      ...(search.period === '3y' || search.period === 'all' ? { period: search.period } : {}),
      ...(search.oneOffs === 'all' ? { oneOffs: 'all' as const } : {}), ...detailSearch(search),
    }),
  component: lazyRouteComponent(() => import('@/features/income/IncomeSourceDetailPage'), 'IncomeSourceDetailPage'),
});

// Loaded on demand, all three, as the loans' forms: nothing of the properties weighs on the initial bundle (3D1 design §3).
const propertiesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/properties',
  beforeLoad: requireModule('realestate'),
  validateSearch: (search: Record<string, unknown>): PropertiesSearch => ({
    ...(search.history === '12m' || search.history === 'all' ? { history: search.history } : {}),
    ...(typeof search.property === 'string' && search.property ? { property: search.property } : {}),
    ...detailSearch(search),
  }),
  component: lazyRouteComponent(() => import('@/features/properties/PropertiesPage'), 'PropertiesPage'),
});

const propertyNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/properties/new',
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/properties/new/NewProperty'), 'NewProperty'),
});

const propertyDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/properties/$id',
  validateSearch: detailSearch,
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/properties/PropertyDetailPage'), 'PropertyDetailPage'),
});

const loansRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/loans',
  beforeLoad: requireModule('realestate'),
  validateSearch: (search: Record<string, unknown>): LoansSearch => ({
    ...(search.ended === 1 || search.ended === '1' ? { ended: 1 as const } : {}),
    // The timeline's window; « Tout » stays out of the URL.
    ...(search.span === '5y' || search.span === '10y' ? { span: search.span } : {}),
    ...detailSearch(search),
  }),
  component: lazyRouteComponent(() => import('@/features/loans/LoansPage'), 'LoansPage'),
});

// Loaded on demand, as the income's: the forms, the schedule and their libraries stay out of the initial bundle.
const loanNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/loans/new',
  validateSearch: (search: Record<string, unknown>): NewLoanSearch =>
    (typeof search.property === 'string' && UUID.test(search.property) ? { property: search.property } : {}),
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/loans/new/NewLoan'), 'NewLoan'),
});

const loanDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/loans/$id',
  validateSearch: detailSearch,
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/loans/LoanDetailPage'), 'LoanDetailPage'),
});

// Loaded on demand, as the detail: the contract form and its libraries stay out of the initial bundle (3B2 design §5).
const loanContractRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/loans/$id/contract',
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/loans/contract/ContractPage'), 'ContractPage'),
});

// Loaded on demand: the printable receipt is read once, then printed.
const propertyReceiptRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/properties/$id/receipt',
  // A malformed month would be a 422: it is dropped, and the server picks the default month.
  validateSearch: (search: Record<string, unknown>): ReceiptSearch =>
    ({ month: typeof search.month === 'string' && /^\d{4}-(0[1-9]|1[0-2])$/.test(search.month) ? search.month : undefined }),
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/properties/receipt/ReceiptPage'), 'ReceiptPage'),
});

// Loaded on demand: the reminder letter is read once, then printed.
const propertyArrearsLetterRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/properties/$id/arrears-letter',
  validateSearch: (search: Record<string, unknown>): ArrearsLetterSearch => {
    const days = Number(search.days);
    // An explicit `undefined`: the router keeps the raw keys a validator leaves out.
    return { days: Number.isInteger(days) && days >= 1 && days <= 60 ? days : undefined };
  },
  beforeLoad: requireModule('realestate'),
  component: lazyRouteComponent(() => import('@/features/properties/arrears/ArrearsLetterPage'), 'ArrearsLetterPage'),
});

// Loaded on demand like the import: the forms and their libraries stay out of the initial bundle (1E2a design §5).
const subscriptionNewRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/subscriptions/new',
  beforeLoad: requireModule('subscriptions'),
  component: lazyRouteComponent(() => import('@/features/subscriptions/NewSubscription'), 'NewSubscription'),
});

const subscriptionDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/subscriptions/$id',
  beforeLoad: requireModule('subscriptions'),
  component: lazyRouteComponent(() => import('@/features/subscriptions/SubscriptionDetailPage'), 'SubscriptionDetailPage'),
});

// Loaded on demand (web design §6.2): the wizard and its canonicalizer stay out of the initial bundle.
const importRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/import',
  beforeLoad: requireModule('import'),
  component: lazyRouteComponent(() => import('@/features/import/ImportLayout'), 'ImportLayout'),
});

const importHomeRoute = createRoute({
  getParentRoute: () => importRoute,
  path: '/',
  component: lazyRouteComponent(() => import('@/features/import/ImportHome'), 'ImportHome'),
});

const importSessionRoute = createRoute({
  getParentRoute: () => importRoute,
  path: '$id',
  component: lazyRouteComponent(() => import('@/features/import/ImportSession'), 'ImportSession'),
});

// Loaded on demand: the export and its assembly stay out of the initial bundle. Every adult may export (1E4 design §1).
const exportRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/export',
  beforeLoad: requireModule('core'),
  component: lazyRouteComponent(() => import('@/features/export/ExportPage'), 'ExportPage'),
});

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute, path: '/settings', beforeLoad: () => { redirect({ to: '/settings/appearance', throw: true }); },
});

const appearanceRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings/appearance', component: lazyRouteComponent(() => import('@/features/settings/AppearancePage'), 'AppearancePage') });
const projectionSettingsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings/projection', component: lazyRouteComponent(() => import('@/features/settings/ProjectionSettingsPage'), 'ProjectionSettingsPage') });
/** The bank's callback lands here with `connected` (the new connection) or `error` (a code): the page reads them once. */
const banksSettingsRoute = createRoute({
  getParentRoute: () => rootRoute, path: '/settings/banks',
  validateSearch: (search: Record<string, unknown>): BanksSearch => ({
    ...(typeof search.connected === 'string' ? { connected: search.connected } : {}),
    ...(typeof search.error === 'string' ? { error: search.error } : {}),
  }),
  component: lazyRouteComponent(() => import('@/features/settings/banks/BanksPage'), 'BanksPage'),
});
const notificationSettingsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings/notifications', component: lazyRouteComponent(() => import('@/features/settings/NotificationSettingsPage'), 'NotificationSettingsPage') });

const securityRoute = createRoute({ getParentRoute: () => rootRoute, path: '/security', component: lazyRouteComponent(() => import('@/features/security/SecurityPage'), 'SecurityPage') });
const moreRoute = createRoute({ getParentRoute: () => rootRoute, path: '/more', component: lazyRouteComponent(() => import('./MorePage'), 'MorePage') });

const proposalsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/proposals',
  validateSearch: (search: Record<string, unknown>): ProposalsSearch =>
    search.status === 'approved' || search.status === 'rejected' ? { status: search.status } : {},
  component: lazyRouteComponent(() => import('@/features/proposals/ProposalsPage'), 'ProposalsPage'),
});

const proposalDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/proposals/$id',
  component: lazyRouteComponent(() => import('@/features/proposals/ProposalDetailPage'), 'ProposalDetailPage'),
});

const forbiddenRoute = createRoute({ getParentRoute: () => rootRoute, path: '/forbidden', component: ForbiddenPage });

/** The admin area is routed for admins only; the server refuses anyway (authorization.md §6). */
const requireAdmin = ({ context }: { context: RouterContext }) => {
  if (!context.capabilities.admin) redirect({ to: '/forbidden', throw: true });
};

const adminRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/admin',
  beforeLoad: ({ context }) => {
    requireAdmin({ context });
    redirect({ to: '/admin/users', throw: true });
  },
});

const adminUsersRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/users', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/UsersPage'), 'UsersPage') });
const adminHouseholdRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/household', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/HouseholdPage'), 'HouseholdPage') });
const adminFxRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/fx', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/FxPage'), 'FxPage') });
const adminPricesRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/prices', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/PricesPage'), 'PricesPage') });
const adminIrlRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/irl', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/IrlPage'), 'IrlPage') });
const adminBankingRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/banking', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/BankingPage'), 'BankingPage') });
const adminCatalogRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/catalog', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/CatalogPage'), 'CatalogPage') });
const adminJobsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/jobs', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/JobsPage'), 'JobsPage') });
const adminSystemRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/system', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/SystemPage'), 'SystemPage') });
const adminNotificationsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/notifications', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/NotificationsAdminPage'), 'NotificationsAdminPage') });
const adminSecurityRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/security', beforeLoad: requireAdmin, component: lazyRouteComponent(() => import('@/features/admin/SecurityJournalPage'), 'SecurityJournalPage') });

export const routeTree = rootRoute.addChildren([
  indexRoute, dashboardRoute, subscriptionsRoute, subscriptionNewRoute, subscriptionDetailRoute, investmentsRoute, investmentNewRoute, investmentDetailRoute, holdingDetailRoute,
  goalsRoute, goalNewRoute, goalDetailRoute, documentsRoute, documentDetailRoute, incomeRoute, incomeNewRoute, incomeDetailRoute, taxRoute, taxAssessmentNewRoute, taxAssessmentEditRoute, scenariosRoute, scenarioNewRoute, projectionScenarioRoute, scenarioDetailRoute, propertiesRoute, propertyNewRoute, propertyDetailRoute, loansRoute, loanNewRoute, loanDetailRoute, loanContractRoute, propertyReceiptRoute, propertyArrearsLetterRoute, notificationsRoute, routineRoute, proposalsRoute, proposalDetailRoute,
  importRoute.addChildren([importHomeRoute, importSessionRoute]), exportRoute, settingsRoute, appearanceRoute, notificationSettingsRoute, projectionSettingsRoute, banksSettingsRoute,
  securityRoute, operationsRoute, moreRoute, forbiddenRoute, adminRoute, adminUsersRoute, adminHouseholdRoute, adminFxRoute, adminPricesRoute, adminIrlRoute, adminBankingRoute, adminCatalogRoute, adminJobsRoute,
  adminSystemRoute, adminNotificationsRoute, adminSecurityRoute,
]);

export function createAppRouter(context: RouterContext, history?: RouterHistory) {
  return createRouter({ routeTree, context, ...(history ? { history } : {}), defaultPreload: 'intent' });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
  /**
   * What a page landing on another says it did: the contract page lands on the loan's detail (3B2 design §5); the household's
   * early repayment hands a loan its part, to prefill its repayment form once (loans dashboard §5.3).
   */
  interface HistoryState {
    loanSaved?: 'contract';
    loanRepayment?: { onDate: string; amount: string; effect: 'reduce_term' | 'reduce_payment'; penalty: string };
  }
}

export function AppRouter() {
  const { capabilities } = useSession();
  const [router] = useState(() => createAppRouter({ capabilities }));
  return <RouterProvider router={router} />;
}
