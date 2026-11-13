import type { Api } from '@/api/client';
import { ProblemError, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';

export type Preferences = components['schemas']['Preferences'];

export type Session = {
  person: components['schemas']['SessionPerson'];
  csrf: string;
  sessionEpoch: number;
  preferences: Preferences;
  /** Demo mode (issue 203): every screen says the data is invented and reset each night. */
  demo: boolean;
};

/** `null` when nobody is signed in; throws (a `ProblemError` when the API answered) when the session cannot be read at all. */
export async function loadSession(api: Api): Promise<Session | null> {
  const { data, error } = await api.fetchClient.GET('/auth/session');
  if (!data) throw new ProblemError(toProblem(error));
  if (!data.authenticated || !data.person || !data.csrf || data.sessionEpoch == null || !data.preferences) return null;
  return { person: data.person, csrf: data.csrf, sessionEpoch: data.sessionEpoch, preferences: data.preferences, demo: data.demo ?? false };
}

/** Where signing out lands: rendered without the login redirect, it says what stays open (SignedOutPage). */
export const SIGNED_OUT_PATH = '/signed-out';

/** Where the server sends an identity no administrator added (identity-and-access/authentication.md §5). */
export const LOGIN_REFUSED_PATH = '/login-refused';

/** Where the server sends the browser when the identity provider does not answer (issue 363). */
export const LOGIN_UNAVAILABLE_PATH = '/login-unavailable';

/** Demo mode (issue 203): where /auth/login sends the browser, to pick an adult of the demo household. */
export const DEMO_PATH = '/demo';

/** The demo sign-in (DemoEndpoints): a relative return path only, as /auth/login. */
export const demoSignInUrl = (key: string, returnUrl: string | null): string =>
  `/auth/demo/${key}?returnUrl=${encodeURIComponent(returnUrl ?? '/')}`;

/** A relative path only: /auth/login refuses anything else (ReturnUrl.IsSafe). */
export const loginUrl = (location: Pick<Location, 'pathname' | 'search'>): string =>
  `/auth/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`;
