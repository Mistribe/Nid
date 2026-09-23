import { createRootRouteWithContext, createRoute, createRouter, redirect, RouterProvider, type RouterHistory } from '@tanstack/react-router';
import { useState } from 'react';
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

export const routeTree = rootRoute.addChildren([
  indexRoute, subscriptionsRoute, importRoute, settingsRoute, appearanceRoute, forbiddenRoute,
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
