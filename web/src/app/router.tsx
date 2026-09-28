import {
  createRootRouteWithContext,
  createRoute,
  createRouter,
  lazyRouteComponent,
  redirect,
  RouterProvider,
  type RouterHistory,
} from '@tanstack/react-router';
import { useState } from 'react';
import { CatalogPage } from '@/features/admin/CatalogPage';
import { FxPage } from '@/features/admin/FxPage';
import { HouseholdPage } from '@/features/admin/HouseholdPage';
import { JobsPage } from '@/features/admin/JobsPage';
import { SecurityJournalPage } from '@/features/admin/SecurityJournalPage';
import { SystemPage } from '@/features/admin/SystemPage';
import { UsersPage } from '@/features/admin/UsersPage';
import { SecurityPage } from '@/features/security/SecurityPage';
import { AppearancePage } from '@/features/settings/AppearancePage';
import { SubscriptionsPage } from '@/features/subscriptions/SubscriptionsPage';
import { ForbiddenPage } from './ForbiddenPage';
import { NotFoundPage } from './NotFoundPage';
import { useSession, type Capabilities } from './Providers';
import { Shell } from './Shell';

export type RouterContext = { capabilities: Capabilities };

/** Foyer is the default and stays out of the URL; Moi is `?scope=me` (screens.md §1). */
export type RootSearch = { scope?: 'me' };

/** The list's own filters, in the URL beside the root scope (1E2a design §3; labels comma-joined, 1E2b2 design §3.3). */
export type SubscriptionsSearch = { status?: 'active' | 'ended'; q?: string; label?: string };

export function homePath(capabilities: Capabilities): '/subscriptions' | '/import' | '/settings/appearance' {
  if (capabilities.modules.includes('subscriptions')) return '/subscriptions';
  if (capabilities.modules.includes('import')) return '/import';
  return '/settings/appearance';
}

const requireModule = (module: string) => ({ context }: { context: RouterContext }) => {
  if (!context.capabilities.modules.includes(module)) redirect({ to: homePath(context.capabilities), throw: true });
};

const rootRoute = createRootRouteWithContext<RouterContext>()({
  validateSearch: (search: Record<string, unknown>): RootSearch => (search.scope === 'me' ? { scope: 'me' } : {}),
  component: Shell,
  notFoundComponent: NotFoundPage,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  beforeLoad: ({ context }) => { redirect({ to: homePath(context.capabilities), throw: true }); },
});

const subscriptionsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/subscriptions',
  beforeLoad: requireModule('subscriptions'),
  validateSearch: (search: Record<string, unknown>): SubscriptionsSearch => ({
    ...(search.status === 'active' || search.status === 'ended' ? { status: search.status } : {}),
    ...(typeof search.q === 'string' && search.q !== '' ? { q: search.q } : {}),
    ...(typeof search.label === 'string' && search.label !== '' ? { label: search.label } : {}),
  }),
  component: SubscriptionsPage,
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

const appearanceRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings/appearance', component: AppearancePage });

const securityRoute = createRoute({ getParentRoute: () => rootRoute, path: '/security', component: SecurityPage });

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

const adminUsersRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/users', beforeLoad: requireAdmin, component: UsersPage });
const adminHouseholdRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/household', beforeLoad: requireAdmin, component: HouseholdPage });
const adminFxRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/fx', beforeLoad: requireAdmin, component: FxPage });
const adminCatalogRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/catalog', beforeLoad: requireAdmin, component: CatalogPage });
const adminJobsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/jobs', beforeLoad: requireAdmin, component: JobsPage });
const adminSystemRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/system', beforeLoad: requireAdmin, component: SystemPage });
const adminSecurityRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/security', beforeLoad: requireAdmin, component: SecurityJournalPage });

export const routeTree = rootRoute.addChildren([
  indexRoute, subscriptionsRoute, subscriptionNewRoute, subscriptionDetailRoute, importRoute.addChildren([importHomeRoute, importSessionRoute]), exportRoute, settingsRoute, appearanceRoute,
  securityRoute, forbiddenRoute, adminRoute, adminUsersRoute, adminHouseholdRoute, adminFxRoute, adminCatalogRoute, adminJobsRoute,
  adminSystemRoute, adminSecurityRoute,
]);

export function createAppRouter(context: RouterContext, history?: RouterHistory) {
  return createRouter({ routeTree, context, ...(history ? { history } : {}), defaultPreload: 'intent' });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}

export function AppRouter() {
  const { capabilities } = useSession();
  const [router] = useState(() => createAppRouter({ capabilities }));
  return <RouterProvider router={router} />;
}
