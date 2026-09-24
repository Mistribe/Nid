import { QueryClient } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { createApi, type Api } from '@/api/client';
import { isProblem, present, toProblem } from '@/api/errors';
import { App } from './App';
import { LoginRefusedPage } from './LoginRefusedPage';
import { Providers, type AppLocation, type Capabilities } from './Providers';
import { loadSession, loginUrl, LOGIN_REFUSED_PATH, SIGNED_OUT_PATH, type Session } from './session';
import { SignedOutPage } from './SignedOutPage';
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

/** The signed-out page reads the session only for the portal's address; without it, it offers no portal link. */
async function renderSignedOut(root: HTMLElement, api: Api): Promise<Root> {
  let identityProvider: string | null = null;
  try {
    const { data } = await api.fetchClient.GET('/auth/session');
    identityProvider = data?.identityProvider ?? null;
  } catch {
    // Unreachable API: the page still says you are signed out, just without the link.
  }
  const reactRoot = createRoot(root);
  reactRoot.render(
    <StrictMode>
      <SignedOutPage identityProvider={identityProvider} />
    </StrictMode>,
  );
  return reactRoot;
}

/** No session exists, and the login redirect would loop through the identity provider: render the page as it is. */
function renderLoginRefused(root: HTMLElement, location: AppLocation): Root {
  const email = new URLSearchParams(location.search).get('email');
  const reactRoot = createRoot(root);
  reactRoot.render(
    <StrictMode>
      <LoginRefusedPage email={email} />
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

  // Never the login redirect here: the identity provider's open session would sign the same person straight back in.
  if (location.pathname === SIGNED_OUT_PATH) return renderSignedOut(root, api);
  if (location.pathname === LOGIN_REFUSED_PATH) return renderLoginRefused(root, location);

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
