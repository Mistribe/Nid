import { useEffect, useState } from 'react';
import type { TFunction } from 'i18next';
import { createApi, type Api } from '@/api/client';

/**
 * Issue 215: the pages before any session (sign-in, setup, password link) talk to the API with their own client. A 401
 * there is a wrong password, not an expired session: it must not send the browser back to /auth/login. The CSRF token
 * is the pre-session one /auth/session hands out (local-accounts.md §11).
 */
export function createPublicApi(): { api: Api; setCsrf: (token: string | null) => void } {
  let csrf: string | null = null;
  return { api: createApi({ getCsrf: () => csrf, onUnauthenticated: () => {} }), setCsrf: (token) => { csrf = token; } };
}

export type PreSession = {
  /** Someone is already signed in on this browser. */
  authenticated: boolean;
  methods: string[];
  setupRequired: boolean;
};

/** Reads /auth/session once, which sets the pre-session cookie and gives its CSRF token. `null` while loading; `'failed'` when unreadable. */
export function usePreSession(api: Api, setCsrf: (token: string | null) => void): PreSession | null | 'failed' {
  const [state, setState] = useState<PreSession | null | 'failed'>(null);
  useEffect(() => {
    let live = true;
    api.fetchClient.GET('/auth/session').then(({ data }) => {
      if (!live) return;
      if (!data) { setState('failed'); return; }
      setCsrf(data.authenticated ? null : data.csrf ?? null);
      setState({ authenticated: data.authenticated, methods: data.methods, setupRequired: data.setupRequired });
    }, () => { if (live) setState('failed'); });
    return () => { live = false; };
  }, [api, setCsrf]);
  return state;
}

/** The secret a setup or password link carries in its fragment, which the browser never sends to a server. */
export function fragmentToken(hash: string, prefix: string): string | null {
  const token = hash.startsWith('#') ? hash.slice(1) : hash;
  return token.startsWith(prefix) && token.length > prefix.length ? token : null;
}

/** The server's code, from a Problem Details body. */
export function codeOf(error: unknown): string | null {
  return typeof error === 'object' && error !== null && 'code' in error && typeof error.code === 'string' ? error.code : null;
}

const KNOWN = new Set([
  'auth.invalid_credentials', 'auth.https_required', 'auth.invalid_setup_token', 'auth.setup_closed', 'auth.link_unusable',
  'auth.login_invalid', 'auth.password_too_short', 'auth.password_too_long', 'auth.password_too_common', 'auth.current_password_wrong',
  'auth.invalid_code', 'auth.mfa_expired', 'auth.totp_already_enabled', 'core.login_taken', 'core.name_required',
]);

/** What a refusal says in French: the server's English title is for logs and developers. */
export function refusal(t: TFunction, error: unknown, status?: number): string {
  if (status === 429) return t('local_auth.error.too_many');
  if (status === 403) return t('local_auth.error.forbidden');
  const code = codeOf(error);
  return code && KNOWN.has(code) ? t(`local_auth.error.${code.replace('.', '_')}` as 'local_auth.error.too_many') : t('local_auth.error.unknown');
}
