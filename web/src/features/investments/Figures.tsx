import { Clock } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Direction } from '@/components/Direction';
import { Badge } from '@/components/ui/badge';
import type { Figures } from '@/api/investments';

export const pct = (value: number) => new Intl.NumberFormat('fr-FR', { signDisplay: 'exceptZero', minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(value);

/** The gain in euros and percent; unknown figures are said, never shown as zero (spec §4). */
export function Gain({ figures }: { figures: Figures }) {
  const { t } = useTranslation();
  if (!figures.gain) return <span className="text-muted-foreground">{t('investments.no_value')}</span>;
  return (
    <Direction amount={figures.gain}>
      {/* Secondary to the amount, as in HeroChange; when the column is narrow it moves to the next line whole. */}
      <GainPct value={figures.gainPct} className="font-normal text-muted-foreground" />
    </Direction>
  );
}

/** The gain's percent alone, « — » when the server gives none: under the amount in a StatLine (#376). */
export function GainPct({ value, className }: { value: number | null | undefined; className?: string }) {
  const { t } = useTranslation();
  return value === null || value === undefined
    ? <><span aria-hidden="true">{t('investments.dash')}</span><span className="sr-only">{t('investments.pct_undefined')}</span></>
    : <span className={`tabular-nums whitespace-nowrap ${className ?? ''}`}>{t('investments.pct', { value: pct(value) })}</span>;
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
