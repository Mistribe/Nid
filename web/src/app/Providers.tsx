import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { ApiContext } from '@/api/ApiProvider';
import type { Api } from '@/api/client';
import type { components } from '@/api/schema';
import { SIGNED_OUT_PATH, type Preferences, type Session } from './session';

export type Capabilities = components['schemas']['CapabilitiesView'];
export type AppLocation = Pick<Location, 'pathname' | 'search' | 'assign' | 'reload'>;

type SessionState = {
  session: Session;
  capabilities: Capabilities;
  setPreferences: (preferences: Preferences | ((current: Preferences) => Preferences)) => void;
  signOut: () => Promise<void>;
  /** A full reload of the app, for the rare write whose effect the running app cannot reflect on its own (a self-demotion leaves capabilities stale). */
  reload: () => void;
  /** Leaves for the signed-out page: never through login, which the identity provider would answer by signing in again. */
  leave: () => void;
  /** Leaves the app for another site's page (a bank's authorisation), which sends the browser back here. */
  goTo: (url: string) => void;
};

const SessionContext = createContext<SessionState | null>(null);

export function Providers(props: {
  api: Api;
  queryClient: QueryClient;
  session: Session;
  capabilities: Capabilities;
  location: AppLocation;
  children: ReactNode;
}) {
  const { api, queryClient, capabilities, location, children } = props;
  const [session, setSession] = useState(props.session);

  const value = useMemo<SessionState>(() => ({
    session,
    capabilities,
    setPreferences: (preferences) => setSession((current) =>
      ({ ...current, preferences: typeof preferences === 'function' ? preferences(current.preferences) : preferences })),
    signOut: async () => {
      const { response } = await api.fetchClient.POST('/auth/logout');
      if (!response.ok) return; // still signed in: leaving would pretend otherwise
      queryClient.clear();
      location.assign(SIGNED_OUT_PATH);
    },
    reload: () => location.reload(),
    leave: () => {
      queryClient.clear();
      location.assign(SIGNED_OUT_PATH);
    },
    goTo: (url) => location.assign(url),
  }), [session, capabilities, api, queryClient, location]);

  return (
    <ApiContext.Provider value={api}>
      <QueryClientProvider client={queryClient}>
        <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
      </QueryClientProvider>
    </ApiContext.Provider>
  );
}

export function useSession(): SessionState {
  const state = useContext(SessionContext);
  if (!state) throw new Error('useSession() used outside <Providers>.');
  return state;
}
