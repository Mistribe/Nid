import { createRootRouteWithContext, createRoute, createRouter, redirect, RouterProvider, type RouterHistory } from '@tanstack/react-router';
import { useState } from 'react';
import { FxPage } from '@/features/admin/FxPage';
import { HouseholdPage } from '@/features/admin/HouseholdPage';
import { JobsPage } from '@/features/admin/JobsPage';
import { SystemPage } from '@/features/admin/SystemPage';
import { UsersPage } from '@/features/admin/UsersPage';
import { ImportPage } from '@/features/import/ImportPage';
import { AppearancePage } from '@/features/settings/AppearancePage';
import { SubscriptionsPage } from '@/features/subscriptions/SubscriptionsPage';
import { ForbiddenPage } from './ForbiddenPage';
import { NotFoundPage } from './NotFoundPage';
import { useSession, type Capabilities } from './Providers';
import { Shell } from './Shell';

export type RouterContext = { capabilities: Capabilities };

/** Foyer is the default and stays out of the URL; Moi is `?scope=me` (screens.md §1). */
export type RootSearch = { scope?: 'me' };

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
  getParentRoute: () => rootRoute, path: '/subscriptions', beforeLoad: requireModule('subscriptions'), component: SubscriptionsPage,
});

const importRoute = createRoute({
  getParentRoute: () => rootRoute, path: '/import', beforeLoad: requireModule('import'), component: ImportPage,
});

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute, path: '/settings', beforeLoad: () => { redirect({ to: '/settings/appearance', throw: true }); },
});

const appearanceRoute = createRoute({ getParentRoute: () => rootRoute, path: '/settings/appearance', component: AppearancePage });

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
const adminJobsRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/jobs', beforeLoad: requireAdmin, component: JobsPage });
const adminSystemRoute = createRoute({ getParentRoute: () => rootRoute, path: '/admin/system', beforeLoad: requireAdmin, component: SystemPage });

export const routeTree = rootRoute.addChildren([
  indexRoute, subscriptionsRoute, importRoute, settingsRoute, appearanceRoute, forbiddenRoute, adminRoute, adminUsersRoute,
  adminHouseholdRoute, adminFxRoute, adminJobsRoute, adminSystemRoute,
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
