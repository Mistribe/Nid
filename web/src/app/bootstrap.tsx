import { QueryClient } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createApi } from '@/api/client';
import { isProblem } from '@/api/errors';
import { App } from './App';
import { Providers, type AppLocation } from './Providers';
import { loadSession, loginUrl } from './session';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from './theme';

/** No retry on a 4xx: the server has answered, and asking again changes nothing (architecture.md §6). */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { staleTime: 30_000, retry: (failures, error) => !(isProblem(error) && error.status < 500) && failures < 2 },
    },
  });
}

/** frontend/architecture.md §3: session, preferences, capabilities, then the app. */
export async function bootstrap({ root, location }: { root: HTMLElement; location: AppLocation }): Promise<void> {
  let csrf: string | null = null;
  const queryClient = createQueryClient();
  const api = createApi({
    getCsrf: () => csrf,
    onUnauthenticated: () => {
      queryClient.clear();
      location.assign(loginUrl(location));
    },
  });

  const session = await loadSession(api);
  if (!session) {
    location.assign(loginUrl(location));
    return;
  }
  csrf = session.csrf;

  // The server's preference wins over the local copy theme-init.js applied, and replaces it.
  const theme = resolveTheme(session.preferences.theme);
  const mode = resolveMode(session.preferences.mode);
  applyTheme(theme, mode);
  rememberTheme(theme, mode);

  const { data: capabilities } = await api.fetchClient.GET('/api/v1/platform/capabilities');

  createRoot(root).render(
    <StrictMode>
      <Providers api={api} queryClient={queryClient} session={session} capabilities={capabilities ?? { modules: [] }} location={location}>
        <App />
      </Providers>
    </StrictMode>,
  );
}
