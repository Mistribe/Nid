import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { StatLine } from '@/components/page/StatLine';
import { percent } from '@/features/dashboard/format';
import { todayIso } from '../../subscriptions/today';
import type { IncomeSourceOverview } from '../api';
import { Change, Sub } from '../overview/OverviewFigures';
import '@/locales/income';

/**
 * Income dashboard §4: the source's key figures as the server computed them — the smoothed monthly income and each
 * running component's part, the next payment, the change over a year and since the start, the cash received this year.
 */
export function SourceFigures({ figures }: { figures: IncomeSourceOverview['figures'] }) {
  const { t } = useTranslation();
  const { monthlyNow, monthlyParts, nextOn, nextAmount, nextLabel, change1y, changeSinceStart, receivedThisYear, oneOffsThisYear } = figures;
  return (
    <section aria-label={t('income_detail.figures')}>
      <StatLine items={[
        {
          label: t('income_detail.per_month'),
          value: (
            <>
              {formatMoney(monthlyNow)}
              <Sub>{t('income_detail.smoothed')}</Sub>
              {monthlyParts.length === 0 ? <Sub>{t('income_detail.no_running')}</Sub> : (
                <ul aria-label={t('income_detail.running_list')} className="text-sm font-normal text-muted-foreground">
                  {monthlyParts.map((p) => <li key={p.name}>{t('income_detail.running_part', { label: p.name, amount: formatMoney(p.amount) })}</li>)}
                </ul>
              )}
            </>
          ),
        },
        {
          label: t('income_detail.next'),
          value: nextOn && nextAmount
            ? <>{t('income_detail.next_line', { date: formatLocalDate(nextOn), amount: formatMoney(nextAmount) })}{nextLabel ? <Sub>{nextLabel}</Sub> : null}</>
            : '—',
        },
        {
          label: t('income_detail.over_1y'),
          value: (
            <>
              {change1y != null ? <Change value={change1y} /> : '—'}
              {changeSinceStart != null ? <Sub>{t('income_detail.since_start', { value: percent(changeSinceStart, true) })}</Sub> : null}
            </>
          ),
        },
        {
          label: t('income_detail.received', { year: todayIso().slice(0, 4) }),
          value: <>{formatMoney(receivedThisYear)}<Sub>{t('income_detail.received_one_offs', { amount: formatMoney(oneOffsThisYear) })}</Sub></>,
        },
      ]} />
    </section>
  );
}
