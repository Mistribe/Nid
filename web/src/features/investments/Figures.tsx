import { ArrowDownRight, ArrowRight, ArrowUpRight, Clock } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge } from '@/components/ui/badge';
import type { Figures } from './api';
import { directionOf } from './format';

const pct = (value: number) => new Intl.NumberFormat('fr-FR', { signDisplay: 'exceptZero', minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value);

const ICON = { up: ArrowUpRight, down: ArrowDownRight, flat: ArrowRight } as const;
const TONE = { up: 'text-status-good', down: 'text-status-critical', flat: 'text-muted-foreground' } as const;
const signed = (locale: string, currency: string, amount: string) =>
  new Intl.NumberFormat(locale, { style: 'currency', currency, signDisplay: 'exceptZero' }).format(amount as `${number}`);

/** An amount with its direction as icon and word, colour only on the icon (never alone, never on text); `children` sit between the amount and the word. */
export function Direction({ amount, children }: { amount: { amount: string; currency: string }; children?: ReactNode }) {
  const { t } = useTranslation();
  const direction = directionOf(amount.amount);
  const Icon = ICON[direction];
  return (
    <span className="inline-flex items-center gap-1">
      <Icon aria-hidden="true" className={`size-4 ${TONE[direction]}`} />
      <span className="tabular-nums">{signed('fr-FR', amount.currency, amount.amount)}</span>
      {children}
      <span>{t(`investments.direction.${direction}`)}</span>
    </span>
  );
}

/** The gain in euros and percent; unknown figures are said, never shown as zero (spec §4). */
export function Gain({ figures }: { figures: Figures }) {
  const { t } = useTranslation();
  if (!figures.gain) return <span className="text-muted-foreground">{t('investments.no_value')}</span>;
  return (
    <Direction amount={figures.gain}>
      {figures.gainPct === null || figures.gainPct === undefined
        ? <><span aria-hidden="true">{t('investments.dash')}</span><span className="sr-only">{t('investments.pct_undefined')}</span></>
        : <span className="tabular-nums">{t('investments.pct', { value: pct(figures.gainPct) })}</span>}
    </Direction>
  );
}

/** The money-weighted annual return the server computed (XIRR), already rounded. */
export function AnnualReturn({ value }: { value: number }) {
  const { t } = useTranslation();
  return <span className="tabular-nums">{t('investments.pct_per_year', { value: pct(value) })}</span>;
}

export function StaleBadge({ days }: { days: number | null | undefined }) {
  const { t } = useTranslation();
  if (days === null || days === undefined) return null;
  return (
    <Badge variant="outline">
      <Clock aria-hidden="true" />
      {t('investments.stale', { count: days })}
    </Badge>
  );
}
