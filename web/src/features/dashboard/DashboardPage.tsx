import { Link, useSearch } from '@tanstack/react-router';
import { Clock } from 'lucide-react';
import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Direction } from '../investments/Figures';
import { DASHBOARD, type DashboardFigure, type DashboardView } from './api';

const percent = (value: number, sign = false) =>
  new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 1, signDisplay: sign ? 'exceptZero' : 'auto' }).format(value);

/** 2D1 design §5: the home page — four figures the server computed for the Foyer / Moi scope. */
export function DashboardPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', DASHBOARD, { params: { query: { scope } } });

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">{t('dashboard.title')}</h1>
      <Body data={answer.data} error={answer.error} />
    </div>
  );
}

function Body({ data, error }: { data?: DashboardView; error: unknown }) {
  const { t } = useTranslation();
  if (!data && error) return <ProblemMessage problem={present(toProblem(error))} />;
  if (!data)
    return (
      <ul aria-busy="true" aria-label={t('dashboard.loading')} className="grid gap-4 md:grid-cols-2">
        {[0, 1, 2, 3].map((i) => <li key={i} className="h-36 animate-pulse rounded-md border bg-muted" />)}
      </ul>
    );
  // Counts, not amounts: nothing at all yet is a welcome, not four « inconnu ».
  if (data.envelopes + data.subscriptions + data.incomeSources === 0)
    return (
      <EmptyState title={t('dashboard.empty_title')} body={t('dashboard.empty_body')}
        action={(
          <span className="flex flex-wrap justify-center gap-3">
            <Link to="/investments/new" className="underline">{t('dashboard.add_envelope')}</Link>
            <Link to="/subscriptions/new" className="underline">{t('dashboard.add_subscription')}</Link>
            <Link to="/income/new" className="underline">{t('dashboard.add_income')}</Link>
          </span>
        )} />
    );
  return (
    <div className="grid gap-4 md:grid-cols-2">
      <NetWorth d={data} />
      <Commitments d={data} />
      <Income d={data} />
      <Change d={data} />
    </div>
  );
}

function Card({ title, to, link, children }: { title: string; to: '/investments' | '/subscriptions' | '/income'; link: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="flex flex-col gap-2 rounded-md border p-4">
      <h2 id={id} className="text-sm font-medium text-muted-foreground">{title}</h2>
      {children}
      <Link to={to} className="mt-auto text-sm underline">{link}</Link>
    </section>
  );
}

/** The big figure, or « Inconnu » with its reason (a missing rate names its currency). */
function Big({ figure, unknown, unknownRate = 'dashboard.unknown_rate' }: { figure: DashboardFigure; unknown: string; unknownRate?: 'dashboard.unknown_rate' | 'dashboard.change_unknown_rate' }) {
  const { t } = useTranslation();
  if (figure.total) return <p className="text-3xl font-semibold tabular-nums">{formatMoney(figure.total)}</p>;
  return <p className="text-lg text-muted-foreground">{figure.missingCurrency ? t(unknownRate, { code: figure.missingCurrency }) : unknown}</p>;
}

function NetWorth({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  return (
    <Card title={t('dashboard.net_worth')} to="/investments" link={t('dashboard.see_investments')}>
      <Big figure={d.netWorth} unknown={t('dashboard.net_worth_unknown')} />
      {d.valuesAsOf ? <p className="text-sm text-muted-foreground">{t('dashboard.values_as_of', { date: formatLocalDate(d.valuesAsOf) })}</p> : null}
      {d.staleEnvelopes > 0 ? (
        <p className="flex items-center gap-1 text-sm"><Clock aria-hidden="true" className="size-4" />{t('dashboard.stale', { count: d.staleEnvelopes })}</p>
      ) : null}
      {d.envelopesWithoutValue > 0 ? <p className="text-sm text-muted-foreground">{t('dashboard.without_value', { count: d.envelopesWithoutValue })}</p> : null}
    </Card>
  );
}

function Commitments({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  return (
    <Card title={t('dashboard.commitments')} to="/subscriptions" link={t('dashboard.see_subscriptions')}>
      <Big figure={d.commitments} unknown={t(d.subscriptions > 0 ? 'dashboard.commitments_none_this_month' : 'dashboard.commitments_none')} />
      {d.commitments.total ? <p className="text-sm text-muted-foreground">{t('dashboard.amortized_per_month')}</p> : null}
      {d.cashThisMonth.total ? <p className="text-sm">{t('dashboard.cash_this_month', { amount: formatMoney(d.cashThisMonth.total) })}</p> : null}
      {d.committedPct != null ? <p className="text-sm">{t('dashboard.committed_share', { value: percent(d.committedPct) })}</p> : null}
    </Card>
  );
}

function Income({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const missing = d.income.missingCurrency ?? d.commitments.missingCurrency;
  return (
    <Card title={t('dashboard.income')} to="/income" link={t('dashboard.see_income')}>
      <Big figure={d.income} unknown={t('dashboard.income_unknown')} />
      {d.income.total ? <p className="text-sm text-muted-foreground">{t('dashboard.smoothed_per_month')}</p> : null}
      <p className="text-sm">{d.margin ? t('dashboard.margin', { amount: formatMoney(d.margin) }) : d.incomeSources > 0 && missing ? t('dashboard.margin_unknown_rate', { code: missing }) : t('dashboard.margin_unknown')}</p>
    </Card>
  );
}

function Change({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const total = d.change.total;
  return (
    <Card title={t('dashboard.change')} to="/investments" link={t('dashboard.see_investments')}>
      {total ? (
        <p className="text-3xl font-semibold">
          <Direction amount={total}>
            {d.changePct != null ? <span className="text-lg tabular-nums">{t('dashboard.pct', { value: percent(d.changePct, true) })}</span> : null}
          </Direction>
        </p>
      ) : <Big figure={d.change} unknown={t('dashboard.change_unknown')} unknownRate="dashboard.change_unknown_rate" />}
      {d.changeContributions.total ? (
        <p className="text-sm text-muted-foreground">{t('dashboard.of_which_paid_in', { amount: formatMoney(d.changeContributions.total) })}</p>
      ) : null}
    </Card>
  );
}
