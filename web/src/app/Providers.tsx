import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createContext, useContext, useMemo, useState, type ReactNode } from 'react';
import { ApiContext } from '@/api/ApiProvider';
import type { Api } from '@/api/client';
import type { components } from '@/api/schema';
import type { Preferences, Session } from './session';

export type Capabilities = components['schemas']['CapabilitiesView'];
export type AppLocation = Pick<Location, 'pathname' | 'search' | 'assign'>;

type SessionState = {
  session: Session;
  capabilities: Capabilities;
  setPreferences: (preferences: Preferences) => void;
  signOut: () => Promise<void>;
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
    setPreferences: (preferences) => setSession((current) => ({ ...current, preferences })),
    signOut: async () => {
      await api.fetchClient.POST('/auth/logout');
      queryClient.clear();
      location.assign('/');
    },
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
