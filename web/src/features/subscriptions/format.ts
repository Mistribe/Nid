import type { TFunction } from 'i18next';
import type { components } from '@/api/schema';
import type { Access } from '@/components/AccessBadge';
import type { Detail } from '@/api/subscriptions';
import '@/locales/subscriptions';

type Person = components['schemas']['PersonDto'];
type Duration = components['schemas']['DurationView'];
type Payer = components['schemas']['PayerView'];

const UNITS = ['day', 'week', 'month', 'year'] as const;
const SOURCES = ['paid', 'bonus', 'included', 'trial', 'gift'] as const;
const KINDS = ['prepaid_term', 'bundle', 'gift', 'adjustment', 'refund'] as const;

/** "1 mois", "2 semaines": a unit the server added later reads as its raw name rather than breaking the page. */
export function durationText(t: TFunction, d: Duration): string {
  const unit = UNITS.find((u) => u === d.unit);
  return unit ? t(`units.${unit}`, { count: d.count }) : `${d.count} ${d.unit}`;
}

export function sourceText(t: TFunction, source: string): string {
  const known = SOURCES.find((s) => s === source);
  return known ? t(`coverage.source.${known}`) : source;
}

export function kindText(t: TFunction, kind: string): string {
  const known = KINDS.find((k) => k === kind);
  return known ? t(`purchase_kind.${known}`) : kind;
}

export function payerText(t: TFunction, payer: Payer, persons: Person[]): string {
  if (payer.type === 'person') return t('payer.person', { name: persons.find((p) => p.id === payer.personId)?.displayName ?? '?' });
  return payer.type === 'external' ? t('payer.external') : t('payer.household');
}

/** Private, or owned by dependents only, or shared: what AccessBadge shows (screens.md §13). */
export function accessOf(detail: Detail, persons: Person[]): Access {
  if (detail.visibility === 'private') return 'private';
  const kinds = detail.owners.map((o) => persons.find((p) => p.id === o.personId)?.kind);
  return kinds.length > 0 && kinds.every((k) => k === 'dependent') ? 'dependent' : 'shared';
}

/** The last covered day of a half-open period: the day before `endBefore` (calendar arithmetic, not amounts). */
export function lastDay(endBefore: string): string {
  const [y, m, d] = endBefore.split('-').map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1, d - 1)).toISOString().slice(0, 10);
}
