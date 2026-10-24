import type { TFunction } from 'i18next';
import forms from '@/locales/fr.forms.json';
import '@/locales/forms';

type Finding = { code: string; message: string };

/** True of every subscription that renews: it only keeps the end of the walk from reading as an expiry, nothing to act on. */
const HIDDEN = new Set(['subscriptions.horizon_reached']);

export const visibleFindings = <F extends Finding>(findings: F[]): F[] => findings.filter((f) => !HIDDEN.has(f.code));

type FindingKey = keyof typeof forms.finding;
const isKnown = (key: string): key is FindingKey => Object.hasOwn(forms.finding, key);

/** A known code reads in French; an unknown one keeps the server's sentence. Dots would read as nesting: `subscriptions.cycle_cap` → `subscriptions_cycle_cap`. */
export function findingText(t: TFunction, finding: Finding): string {
  const key = finding.code.replaceAll('.', '_');
  return isKnown(key) ? t(`finding.${key}`) : finding.message;
}
