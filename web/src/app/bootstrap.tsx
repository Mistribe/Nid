import { QueryClient } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { createApi } from '@/api/client';
import { isProblem, present, toProblem } from '@/api/errors';
import { App } from './App';
import { Providers, type AppLocation, type Capabilities } from './Providers';
import { loadSession, loginUrl, type Session } from './session';
import { StartupFailure } from './StartupFailure';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from './theme';

/** No retry on a 4xx: the server has answered, and asking again changes nothing (architecture.md §6). */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { staleTime: 30_000, retry: (failures, error) => !(isProblem(error) && error.status < 500) && failures < 2 },
    },
  });
}

/** When the session or the capabilities cannot be read, say so and offer to retry — never a blank page, never a guessed module list. */
function renderFailure(root: HTMLElement, location: AppLocation, error: unknown): Root {
  const reactRoot = createRoot(root);
  reactRoot.render(
    <StrictMode>
      <StartupFailure problem={present(toProblem(error))} onRetry={() => location.reload()} />
    </StrictMode>,
  );
  return reactRoot;
}

/** frontend/architecture.md §3: session, preferences, capabilities, then the app. `null` when it redirected to login instead of rendering. */
export async function bootstrap({ root, location }: { root: HTMLElement; location: AppLocation }): Promise<Root | null> {
  let csrf: string | null = null;
  const queryClient = createQueryClient();
  const api = createApi({
    getCsrf: () => csrf,
    onUnauthenticated: () => {
      queryClient.clear();
      location.assign(loginUrl(location));
    },
  });

  let session: Session | null;
  try {
    session = await loadSession(api);
  } catch (error) {
    return renderFailure(root, location, error);
  }
  if (!session) {
    location.assign(loginUrl(location));
    return null;
  }
  csrf = session.csrf;

  // The server's preference wins over the local copy theme-init.js applied, and replaces it.
  const theme = resolveTheme(session.preferences.theme);
  const mode = resolveMode(session.preferences.mode);
  applyTheme(theme, mode);
  rememberTheme(theme, mode);

  let capabilities: Capabilities;
  try {
    const { data, error } = await api.fetchClient.GET('/api/v1/platform/capabilities');
    if (!data) return renderFailure(root, location, error);
    capabilities = data;
  } catch (error) {
    return renderFailure(root, location, error);
  }

  const reactRoot = createRoot(root);
  reactRoot.render(
    <StrictMode>
      <Providers api={api} queryClient={queryClient} session={session} capabilities={capabilities} location={location}>
        <App />
      </Providers>
    </StrictMode>,
  );
  return reactRoot;
}
