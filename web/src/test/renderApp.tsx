import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers, type Capabilities } from '@/app/Providers';
import { createAppRouter } from '@/app/router';
import type { Session } from '@/app/session';
import { aliceSession, allCapabilities } from './fixtures';

/** The real providers and routes on a memory history, against the MSW handlers of the test. */
export async function renderApp(path: string, options: { capabilities?: Capabilities; session?: Session } = {}) {
  const session = options.session ?? aliceSession;
  const capabilities = options.capabilities ?? allCapabilities;
  const location = { pathname: '/', search: '', assign: vi.fn(), reload: vi.fn() };
  const queryClient = createQueryClient();
  const api = createApi({ getCsrf: () => session.csrf, onUnauthenticated: vi.fn() });
  const router = createAppRouter({ capabilities }, createMemoryHistory({ initialEntries: [path] }));

  const utils = render(
    <Providers api={api} queryClient={queryClient} session={session} capabilities={capabilities} location={location}>
      <RouterProvider router={router} />
    </Providers>,
  );
  await router.load();
  return { ...utils, router, location, queryClient, user: userEvent.setup() };
}
