import type { Api } from '@/api/client';
import type { components } from '@/api/schema';

export type Preferences = components['schemas']['Preferences'];

export type Session = {
  person: components['schemas']['SessionPerson'];
  csrf: string;
  sessionEpoch: number;
  preferences: Preferences;
};

/** `null` when nobody is signed in; throws when the session cannot be read at all. */
export async function loadSession(api: Api): Promise<Session | null> {
  const { data } = await api.fetchClient.GET('/auth/session');
  if (!data) throw new Error('The session could not be read.');
  if (!data.authenticated || !data.person || !data.csrf || data.sessionEpoch == null || !data.preferences) return null;
  return { person: data.person, csrf: data.csrf, sessionEpoch: data.sessionEpoch, preferences: data.preferences };
}

/** A relative path only: /auth/login refuses anything else (ReturnUrl.IsSafe). */
export const loginUrl = (location: Pick<Location, 'pathname' | 'search'>): string =>
  `/auth/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`;
