import type { TFunction } from 'i18next';
import type { components } from '@/api/schema';
import { monthText } from '@/components/LocalDate';
import { AnnualReturn } from './Figures';
import '@/locales/investments';

type RealPercentView = components['schemas']['RealPercentView'];
const REASONS = ['unavailable', 'currency', 'not_published'] as const;
type Reason = (typeof REASONS)[number];

/** #419: the annual return after inflation as a StatLine item; a missing figure says why, nothing when the nominal one is not meaningful. */
export function realReturnItem(t: TFunction, real: RealPercentView | null | undefined) {
  if (!real || (real.percent == null && real.missing == null)) return [];
  const label = t('envelope.real_return');
  if (real.percent == null) return [{ label, value: <span className="text-sm text-muted-foreground">{t(`envelope.real_missing.${REASONS.includes(real.missing as Reason) ? (real.missing as Reason) : 'unavailable'}`)}</span> }];
  return [{ label, value: <AnnualReturn value={real.percent} />, hint: real.inflationTo ? t('envelope.real_return_to', { month: monthText(real.inflationTo) }) : null }];
}
