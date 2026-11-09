import type { TFunction } from 'i18next';
import { formatCurrency } from '@/components/Money';
import series from '@/locales/fr.series.json';
import '@/locales/series';

const KINDS = Object.keys(series.series.kind);

/** The series' name for the person; an unknown kind reads its own name, else its key. */
export function seriesLabel(t: TFunction, s: { kind: string; name?: string | null; key: string }): string {
  if (!KINDS.includes(s.kind)) return s.name ?? s.key;
  const name = s.kind === 'projection_rate' && s.name ? t(`settings.projection.class.${s.name}` as never) : s.name;
  return t(`series.kind.${s.kind}` as never, { name });
}

export function unitLabel(t: TFunction, unit: string): string {
  const [head, rest] = unit.split(':');
  if (head === 'index' || head === 'percent') return t(`series.unit.${head}` as never);
  if (head === 'rate') return t('series.unit.rate', { code: rest });
  return rest ?? unit;
}

const plain = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 10 });
const percent = new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 2 });

/** The server's decimal string as French text; nothing computed. */
export function formatSeriesValue(unit: string, value: string): string {
  if (unit.startsWith('currency:')) return formatCurrency(value, unit.slice('currency:'.length));
  if (unit === 'percent') return `${percent.format(value as `${number}`)} %`;
  return plain.format(value as `${number}`);
}
