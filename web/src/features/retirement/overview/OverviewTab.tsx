import { Link } from '@tanstack/react-router';
import { Suspense, lazy } from 'react';
import { useTranslation } from 'react-i18next';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { Hero } from '@/components/page/Hero';
import { SectionCard } from '@/components/page/SectionCard';
import { StatLine } from '@/components/page/StatLine';
import { buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { yyyyMmLabel as month } from '@/lib/format';
import { Answer, type Scope } from '../Answer';
import type { RetirementView } from '../api';
import '@/locales/retirement';

const IndependenceChart = lazy(() => import('../IndependenceChart'));

/** A server percent as a French one: « 4.0 » reads « 4,0 % » (a string swap, nothing computed). */
const percent = (value: string) => `${value.replace('.', ',')} %`;
const moneyOf = (data: RetirementView) => (amount: string) => formatCurrency(amount, data.currency);
const livingCostMissing = (data: RetirementView) => data.warnings.some((w) => w.code === 'living_cost_missing');

/** Retirement spec §5, the simple view: the monthly gap at the departure, the other figures beside it, then the independence curve. */
export function OverviewTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation();
  return (
    <Answer scope={scope} hero>
      {(data) => {
        const f = data.figures;
        const money = moneyOf(data);
        const missing = livingCostMissing(data);
        return (
          <>
            {f ? (
              <>
                <Hero label={t('retirement.gap')} figure={money(f.gap)} note={t('retirement.gap_note', { month: month(f.month) })} aside={(
                  <StatLine className="xl:grid-cols-4" items={[
                    { label: t('retirement.income_now'), value: money(f.incomeNow) },
                    { label: t('retirement.income_at_retirement'), value: money(f.incomeAtRetirement) },
                    { label: t('retirement.capital_needed'), tip: t('retirement.capital_needed_tip'), value: money(f.capitalNeeded) },
                    {
                      label: t('retirement.independence'),
                      tip: t('retirement.independence_tip'),
                      value: data.financialIndependence ? month(data.financialIndependence) : missing ? t('retirement.independence_not_set') : t('retirement.independence_unreached'),
                    },
                  ]} />
                )} />
                <p className="text-sm text-muted-foreground">{t('retirement.note')}</p>
              </>
            ) : (
              <EmptyState module="/retirement" title={t('retirement.no_figures_title')} body={t('retirement.no_figures')}
                action={<Link to="/settings/projection" className={buttonVariants({ variant: 'outline' })}>{t('retirement.open_settings')}</Link>} />
            )}
            {missing ? <p className="text-sm"><Link to="/settings/projection" className="underline underline-offset-2">{t('retirement.living_cost_missing')}</Link></p> : null}
            <Warnings data={data} />
            {data.independence.length > 0 ? (
              <SectionCard title={t('retirement.independence_curve')}>
                <Suspense fallback={<Skeleton className="h-64 w-full rounded-xl" />}><IndependenceChart data={data} livingCostMissing={missing} /></Suspense>
              </SectionCard>
            ) : null}
          </>
        );
      }}
    </Answer>
  );
}

/** « Analyse »: the capital the projection reaches at the departure beside the one the gap asks for, and the assumptions behind them. */
export function AnalysisTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation();
  return (
    <Answer scope={scope}>
      {(data) => {
        const f = data.figures;
        const a = data.assumptions;
        const money = moneyOf(data);
        return (
          <>
            {f ? (
              <SectionCard title={t('retirement.capital_title')}>
                <StatLine items={[
                  { label: t('retirement.capital_at_retirement'), value: money(f.capitalAtRetirement), hint: month(f.month) },
                  { label: t('retirement.capital_needed'), value: money(f.capitalNeeded), hint: t('retirement.capital_needed_hint', { rate: percent(a.withdrawalRate) }) },
                ]} />
              </SectionCard>
            ) : null}
            <SectionCard title={t('retirement.assumptions')}
              actions={<Link to="/settings/projection" className="text-sm underline underline-offset-2">{t('dashboard.projection.edit_assumptions')}</Link>}>
              <ul className="space-y-1 text-sm">
                <li>{t('settings.projection.withdrawal_rate')} : {percent(a.withdrawalRate)}</li>
                <li>{t('settings.projection.living_cost_summary')} : {Number(a.monthlyLivingCost) > 0 ? money(a.monthlyLivingCost) : t('settings.projection.living_cost_unset')}</li>
                <li>{t('settings.projection.inflation')} : {percent(a.inflation)}</li>
                <li>{t('retirement.horizon')} : <LocalDate value={data.horizonOn} /></li>
              </ul>
            </SectionCard>
          </>
        );
      }}
    </Answer>
  );
}

function Warnings({ data }: { data: RetirementView }) {
  const { t } = useTranslation();
  // living_cost_missing reads in its own link under the hero.
  const shown = data.warnings.filter((w) => w.code !== 'living_cost_missing');
  if (shown.length === 0) return null;
  return (
    <ul className="space-y-0.5 text-sm text-status-warning-text">
      {shown.map((w) => (
        <li key={`${w.code}-${w.detail}`}>
          {w.code === 'income_end_extended' ? t('dashboard.projection.warning_income_end_extended', { name: w.detail })
            : w.code === 'savings_exhausted' ? t('dashboard.projection.warning_savings_exhausted', { month: w.detail ? month(w.detail) : '' })
            : w.code === 'fx_missing' ? t('dashboard.projection.warning_fx_missing', { code: w.detail })
            : w.code}
        </li>
      ))}
    </ul>
  );
}
