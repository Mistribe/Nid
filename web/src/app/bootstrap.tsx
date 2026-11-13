import { MutationCache, QueryClient } from '@tanstack/react-query';
import { StrictMode, type ReactNode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { createApi, type Api } from '@/api/client';
import { isProblem, present, toProblem } from '@/api/errors';
import { PRIVATE_ITEMS } from '@/components/ScopeSwitch';
import { App } from './App';
import { LoginRefusedPage } from './LoginRefusedPage';
import { LoginUnavailablePage } from './LoginUnavailablePage';
import { Providers, type AppLocation, type Capabilities } from './Providers';
import { DemoPage } from './DemoPage';
import { DEMO_PATH, loadSession, loginUrl, LOGIN_REFUSED_PATH, LOGIN_UNAVAILABLE_PATH, SIGNED_OUT_PATH, type Session } from './session';
import { SignedOutPage } from './SignedOutPage';
import { StartupFailure } from './StartupFailure';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from './theme';

/** No retry on a 4xx: the server has answered, and asking again changes nothing (architecture.md §6). */
export function createQueryClient(): QueryClient {
  const client: QueryClient = new QueryClient({
    // Any write may create a private item, which is what shows the Foyer / Moi selector (issue 85).
    mutationCache: new MutationCache({ onSuccess: () => void client.invalidateQueries({ queryKey: ['get', PRIVATE_ITEMS] }) }),
    defaultOptions: {
      queries: { staleTime: 30_000, retry: (failures, error) => !(isProblem(error) && error.status < 500) && failures < 2 },
    },
  });
  return client;
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

/** A page outside the app, rendered as it is: no session exists, and the login redirect would only come back here. */
function renderPage(root: HTMLElement, page: ReactNode): Root {
  const reactRoot = createRoot(root);
  reactRoot.render(<StrictMode>{page}</StrictMode>);
  return reactRoot;
}

/** frontend/architecture.md §3: session, preferences, capabilities, then the app. `null` when it redirected to login instead of rendering. */
export async function bootstrap({ root, location }: { root: HTMLElement; location: AppLocation }): Promise<Root | null> {
  let csrf: string | null = null;
  // Set once the app leaves for the signed-out page: the queries its cleared cache reads again while the browser is still
  // on the page answer 401, and login would have the identity provider sign the same person straight back in.
  let leaving = false;
  const queryClient = createQueryClient();
  const api = createApi({
    getCsrf: () => csrf,
    onUnauthenticated: () => {
      if (leaving) return;
      queryClient.clear();
      location.assign(loginUrl(location));
    },
  });
  const appLocation: AppLocation = {
    get pathname() { return location.pathname; },
    get search() { return location.search; },
    reload: () => location.reload(),
    assign: (url) => {
      leaving ||= url === SIGNED_OUT_PATH;
      location.assign(url);
    },
  };

  // Never the login redirect here: the identity provider's open session would sign the same person straight back in.
  if (location.pathname === SIGNED_OUT_PATH) return renderSignedOut(root, api);
  if (location.pathname === LOGIN_REFUSED_PATH) return renderPage(root, <LoginRefusedPage email={new URLSearchParams(location.search).get('email')} />);
  if (location.pathname === LOGIN_UNAVAILABLE_PATH) return renderPage(root, <LoginUnavailablePage returnUrl={new URLSearchParams(location.search).get('returnUrl')} />);
  if (location.pathname === DEMO_PATH) return renderPage(root, <DemoPage returnUrl={new URLSearchParams(location.search).get('returnUrl')} />);

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
      <Providers api={api} queryClient={queryClient} session={session} capabilities={capabilities} location={appLocation}>
        <App />
      </Providers>
    </StrictMode>,
  );
  return reactRoot;
}
