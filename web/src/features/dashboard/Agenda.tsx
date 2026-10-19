import { Link } from '@tanstack/react-router';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { PendingLine } from '../proposals/PendingLine';
import { usePendingCount } from '../proposals/api';
import { categoryText } from '../properties/format';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import type { DashboardEvent, DashboardView } from './api';
import { ItemLink } from './ItemLink';
import { incomeGaps, isRoutineTodo } from './todo';

function EventText({ e }: { e: DashboardEvent }) {
  const { t } = useTranslation();
  switch (e.kind) {
    case 'subscription_renewal': return <>{t('dashboard.upcoming.subscription_renewal')}</>;
    case 'subscription_expiry': return <>{t('dashboard.upcoming.subscription_expiry')}</>;
    case 'income_phase_end': return <>{t('dashboard.upcoming.income_phase_end', { label: e.label ?? '' })}</>;
    case 'loan_phase_end': return <>{t('dashboard.upcoming.loan_phase_end', { n: e.label ?? '' })}</>;
    case 'loan_end': return <>{t('dashboard.upcoming.loan_end')}</>;
    case 'valuation_due': return <>{t('dashboard.upcoming.valuation_due')}</>;
    case 'property_cost_due': return <>{categoryText(t, e.label)}</>;
    case 'tenancy_end': return <>{t('dashboard.upcoming.tenancy_end', { label: e.label ?? '' })}</>;
    case 'holding_maturity': return <>{t('dashboard.upcoming.holding_maturity', { label: e.label ?? '' })}</>;
    default: return null;
  }
}

/** Spec §4.1: the next 60 days, sorted by the server, date and amount. */
export function Upcoming({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const id = useId();
  return (
    <Card role="region" aria-labelledby={id} data-guide="dashboard.upcoming" className="gap-3 py-4">
      <CardHeader>
        <CardTitle id={id} className="text-base">{t('dashboard.upcoming.title')}</CardTitle>
        <p className="text-xs text-muted-foreground">{t('dashboard.upcoming.window')}</p>
      </CardHeader>
      <CardContent>
        {d.upcoming.length === 0 ? <p className="text-sm text-muted-foreground">{t('dashboard.upcoming.empty')}</p> : (
          <ul className="divide-y text-sm">
            {d.upcoming.map((e) => (
              <li key={`${e.kind}-${e.id}-${e.on}-${e.label ?? ''}`} className="flex flex-wrap gap-x-2 py-2 first:pt-0 last:pb-0">
                <span className="tabular-nums text-muted-foreground">{formatLocalDate(e.on)}</span>
                <ItemLink module={e.module} id={e.id}>{e.name}</ItemLink>
                <span><EventText e={e} /></span>
                {e.amount ? <span className="ml-auto tabular-nums">{formatMoney(e.amount)}</span> : null}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

/** Spec §4.1: one line when something is due (the month's routine, 2E) or proposals wait (4C2); nothing otherwise. */
export function Notice({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const pending = usePendingCount() ?? 0;
  const routine = d.todo.filter(isRoutineTodo).length;
  const gaps = incomeGaps(d.todo);
  if (routine === 0 && gaps.length === 0 && pending === 0) return null;
  return (
    <p className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-lg border bg-muted/40 px-3 py-2 text-sm">
      {routine > 0 ? <Link to="/routine" className="underline">{t('dashboard.notice', { count: routine })}</Link> : null}
      {gaps.length === 1 ? <Link to="/income/$id" params={{ id: gaps[0]!.id }} className="underline">{t('dashboard.notice_income_gap', { count: 1 })}</Link> : null}
      {gaps.length > 1 ? <Link to="/income" className="underline">{t('dashboard.notice_income_gap', { count: gaps.length })}</Link> : null}
      {pending > 0 ? <PendingLine /> : null}
    </p>
  );
}
