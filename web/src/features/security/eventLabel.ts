import type { TFunction } from 'i18next';

const KNOWN = new Set([
  'login', 'logout', 'login_refused', 'access_denied', 'access_violation', 'person_preregistered',
  'person_preregistration_cancelled', 'admin_granted', 'admin_revoked', 'session_revoked', 'household_settings_changed',
]);

/** The one place an event gets its name for people (1F3a spec §7). */
export function eventLabel(t: TFunction, name: string): string {
  return KNOWN.has(name) ? t(`audit_event.${name}` as 'audit_event.login') : t('audit_event.unknown', { name });
}

const KNOWN_VIA = new Set(['ui', 'api', 'job', 'import', 'mcp']);

/** French label for an event's `via` (Minor #6); an origin not yet named here shows as the server sent it. */
export function viaLabel(t: TFunction, via: string): string {
  return KNOWN_VIA.has(via) ? t(`security.via_label.${via}` as 'security.via_label.ui') : via;
}
