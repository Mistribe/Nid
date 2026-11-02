import { useTranslation } from 'react-i18next';
import '@/locales/import';

/** Session states of import-format.md §11.1. */
export const STATES = ['open', 'validated', 'invalid', 'committed', 'cancelled', 'expired'] as const;
export type ImportStateName = (typeof STATES)[number];

/** A session in one of these states is the person's one active import. */
export const ACTIVE_STATES: ReadonlySet<string> = new Set(['open', 'validated', 'invalid']);

const isKnown = (state: string): state is ImportStateName => (STATES as readonly string[]).includes(state);

/** The server applies expiry on use (import-format.md §12bis); a page left open past `expiresAt` applies it too. */
export function effectiveState(session: { state: string; expiresAt: string }, now = Date.now()): string {
  return ACTIVE_STATES.has(session.state) && Date.parse(session.expiresAt) <= now ? 'expired' : session.state;
}

/** A state from the wire, in French; one the SPA does not know is shown as it came. */
export function useStateLabel(): (state: string) => string {
  const { t } = useTranslation();
  return (state) => (isKnown(state) ? t(`import.state.${state}`) : state);
}
