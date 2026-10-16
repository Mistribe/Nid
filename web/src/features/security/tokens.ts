import type { components } from '@/api/schema';

export const TOKENS = '/api/v1/core/me/tokens';
export const TOKEN = '/api/v1/core/me/tokens/{id}';

export type TokenView = components['schemas']['TokenView'];
export type Preset = 'read' | 'propose' | 'direct';
export const PRESETS: readonly Preset[] = ['read', 'propose', 'direct'];
export const EXPIRIES = [30, 90, 365] as const;

const WEEK = 7 * 86_400_000;

/** Spec 4A D5: an active token that expires within 7 days. A date comparison, not an amount. */
export function expiresSoon(token: Pick<TokenView, 'status' | 'expiresAt'>, now = Date.now()): boolean {
  return token.status === 'active' && Date.parse(token.expiresAt) - now < WEEK;
}
