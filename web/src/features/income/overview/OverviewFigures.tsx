import type { ReactNode } from 'react';
import { ArrowDown, ArrowUp } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { StatLine } from '@/components/page/StatLine';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { percent } from '@/features/dashboard/format';
import type { IncomeOverviewFigures } from '../api';

/** A change in words and an arrow (never the colour alone), the sign as the server gave it. */
export function Change({ value, label }: { value: number; label?: string }) {
  const { t } = useTranslation();
  const Icon = value < 0 ? ArrowDown : ArrowUp;
  return (
    <span className="inline-flex items-center gap-1 text-sm font-normal">
      {value === 0 ? null : <Icon aria-hidden="true" className={`size-3.5 ${value < 0 ? 'text-status-critical' : 'text-status-good'}`} />}
      {value === 0 ? null : <span className="sr-only">{value < 0 ? t('income.overview.down') : t('income.overview.up')}</span>}
      {t('income.overview.pct', { value: percent(value, true) })}
      {label ? <span className="text-muted-foreground">{label}</span> : null}
    </span>
  );
}

export const Sub = ({ children }: { children: ReactNode }) => <span className="block text-sm font-normal text-muted-foreground">{children}</span>;

/** The four key figures as the server computed them; one it could not compute reads « — » and says why on focus. */
export function OverviewFigures({ figures, missingRates, period, year }: {
  figures: IncomeOverviewFigures; missingRates: string[]; period: 'year' | '12m' | '3y'; year: number;
}) {
  const { t } = useTranslation();
  // A missing rate nulls a figure before history does: name it (spec §2, §5).
  const why = missingRates.length > 0 ? t('income.overview.missing_rate', { currencies: missingRates.join(', ') }) : t('income.overview.no_history');
  const dash = (
    <Tooltip>
      <TooltipTrigger type="button" aria-label={why} className="rounded outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">—</TooltipTrigger>
      <TooltipContent>{why}</TooltipContent>
    </Tooltip>
  );
  const { monthlyNow, monthlyChange1y, total, elapsed, changeVsPrevious, changeVsPreviousPct, change3y, change3yYearly } = figures;
  const yearly = period === 'year';

  return (
    <TooltipProvider>
      <StatLine items={[
        {
          label: t('income.overview.per_month'),
          value: monthlyNow ? <>{formatMoney(monthlyNow)}{monthlyChange1y != null ? <Change value={monthlyChange1y} label={t('income.overview.over_1y')} /> : null}</> : dash,
        },
        {
          label: yearly ? t('income.overview.total_year', { year }) : t('income.overview.total_period'),
          value: total ? <>{formatMoney(total)}{yearly && elapsed ? <Sub>{t('income.overview.elapsed', { amount: formatMoney(elapsed) })}</Sub> : null}</> : dash,
        },
        {
          label: t('income.overview.vs_previous'),
          value: changeVsPrevious ? <>{formatMoney(changeVsPrevious)}{changeVsPreviousPct != null ? <Change value={changeVsPreviousPct} /> : null}</> : dash,
        },
        {
          label: t('income.overview.last_3y'),
          value: change3y != null
            ? <><Change value={change3y} />{change3yYearly != null ? <Sub>{t('income.overview.yearly', { amount: t('income.overview.pct', { value: percent(change3yYearly, true) }) })}</Sub> : null}</>
            : dash,
        },
      ]} />
    </TooltipProvider>
  );
}
