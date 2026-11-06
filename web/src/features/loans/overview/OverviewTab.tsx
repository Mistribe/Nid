import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { tileOf } from '@/app/nav';
import { present, toProblem } from '@/api/errors';
import type { LoansSearch, RootSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { LocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { OwnerChips } from '@/components/OwnerChips';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { Hero, HeroUnknown } from '@/components/page/Hero';
import { SectionCard } from '@/components/page/SectionCard';
import { SortableTable } from '@/components/page/SortableTable';
import { amountKey } from '@/components/page/sortKey';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { buttonVariants } from '@/components/ui/button';
import { Progress } from '@/components/ui/progress';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { todayIso } from '@/features/subscriptions/today';
import { toPlot } from '@/features/investments/chart/plot';
import { OVERVIEW, type LoanOverviewRow, type LoansOverview } from '../api';
import { money, percent } from '../format';
import { BalanceChart, monthText, useLoanNames } from './BalanceChart';
import { loanColor } from './colors';
import { DurationSection } from './DurationSection';
import { monthsBetween } from './span';
import '@/locales/loans';
import { OverviewSkeleton } from '@/components/page/skeletons';

type Scope = 'household' | 'me';

/** Loans dashboard §3.3: the server's figures, then the curve, the running loans and what changes ahead; nothing computed here. */
export function OverviewTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', OVERVIEW, { params: { query: { scope } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <OverviewSkeleton label={t('loans_overview.loading')} hero />;
  if (d.runningCount === 0 && d.endedCount === 0)
    return (
      <EmptyState module="/loans" title={t('loans.empty_title')} body={t('loans.empty_body')}
        action={<Link to="/loans/new" className={buttonVariants({ variant: 'outline' })}>{t('loans.add')}</Link>} />
    );
  if (d.runningCount === 0)
    return (
      <EmptyState module="/loans" title={t('loans.none_running_title')} body={t('loans.none_running_body')}
        action={(
          <Link to="/loans" search={(prev: RootSearch & LoansSearch) => ({ ...prev, tab: 'list', ended: 1 as const })}
            className={buttonVariants({ variant: 'outline' })}>{t('loans.see_ended')}</Link>
        )} />
    );

  // DOM order is the phone's (figures, progress, curve, upcoming, loans); `md:order-*` puts the loans before what comes.
  return (
    <TooltipProvider>
    <div className="flex flex-col gap-6">
      <BalanceHero d={d} />
      <Figures d={d} />
      <SectionCard title={t('loans_overview.progress_title')}><Advance d={d} /></SectionCard>
      <BalanceChart d={d} />
      <DurationSection d={d} />
      <div className="md:order-last"><Upcoming d={d} /></div>
      <SectionCard title={t('loans_overview.running')}><Running d={d} /></SectionCard>
    </div>
    </TooltipProvider>
  );
}

/** « Inconnu » and why, for a figure a missing rate left out (D4). */
function useFigure(d: LoansOverview) {
  const { t } = useTranslation();
  const codes = [...new Set(d.findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
  return (value: MoneyValue | null | undefined, render: (text: string) => ReactNode = (text) => text): ReactNode =>
    value ? render(formatMoney(value))
      : <span>{t('loans_overview.unknown')}{codes ? <Sub>{t('loans_overview.unknown_rate', { codes })}</Sub> : null}</span>;
}

function Sub({ children }: { children: ReactNode }) {
  return <span className="block text-sm font-normal text-muted-foreground">{children}</span>;
}

/** The balance leads, the months ahead as its curve; the server gives no signed change for it. */
function BalanceHero({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  return (
    <Hero label={t('loans_overview.balance')} figure={d.balance ? figure(d.balance) : <HeroUnknown>{figure(d.balance)}</HeroUnknown>}
      note={d.principal ? t('loans_overview.balance_of', { principal: formatMoney(d.principal) }) : null}
      values={d.months.flatMap((m) => (m.total ? [toPlot(m.total.amount)] : []))} />
  );
}

function Figures({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  const left = d.freeOn ? monthsBetween(todayIso(), d.freeOn) : 0;
  const years = Math.floor(left / 12);
  const months = left % 12;
  const span = years > 0 && months > 0
    ? t('loans_overview.span_both', { years: t('loans_overview.years', { count: years }), months: t('loans_overview.months', { count: months }) })
    : years > 0 ? t('loans_overview.years', { count: years }) : t('loans_overview.months', { count: months });
  return (
    <StatLine items={[
      { label: t('loans_overview.monthly'), value: figure(d.monthlyPayment), hint: t('loans_overview.monthly_note') },
      { label: t('loans_overview.cost'), value: figure(d.costRemaining), hint: d.costPaid ? t('loans_overview.cost_paid', { amount: formatMoney(d.costPaid) }) : null },
      ...(d.freeOn ? [{ label: t('loans_overview.free'), value: monthText(d.freeOn.slice(0, 7)), hint: left > 0 ? t('loans_overview.free_in', { span }) : t('loans_overview.free_this_month') }] : []),
      ...(d.debtRatio ? [{ label: t('loans_overview.debt_ratio'), value: <DebtRatio d={d} ratio={d.debtRatio} /> }] : []),
    ]} />
  );
}

/** Issue #402: the server's ratio, the ceiling it is read against and the room left under it; « Inconnu » and why otherwise. */
function DebtRatio({ d, ratio }: { d: LoansOverview; ratio: NonNullable<LoansOverview['debtRatio']> }) {
  const { t } = useTranslation();
  const figure = useFigure(d);
  if (!ratio.ratio)
    return ratio.unknown === 'no_income'
      ? <span>{t('loans_overview.unknown')}<Sub>{t('loans_overview.debt_ratio_no_income')}</Sub></span>
      : figure(null);
  return (
    <>
      {percent(t, ratio.ratio)}
      <Sub>{t('loans_overview.debt_ratio_ceiling', { ceiling: percent(t, ratio.ceiling) })}</Sub>
      {ratio.room ? <Sub>{t('loans_overview.debt_ratio_room', { amount: formatMoney(ratio.room) })}</Sub> : null}
    </>
  );
}

/** As a loan's own progress: the bar is sized by plotting numbers, the person reads the server's strings. */
function Advance({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  if (!d.principal || !d.principalRepaid || !d.repaidShare) return <p className="text-sm text-muted-foreground">{t('loans_overview.unknown')}</p>;
  const total = toPlot(d.principal.amount);
  const value = total > 0 ? Math.min(100, (toPlot(d.principalRepaid.amount) / total) * 100) : 0;
  const label = t('loans_overview.progress', { repaid: formatMoney(d.principalRepaid), principal: formatMoney(d.principal), share: percent(t, d.repaidShare) });
  return (
    <div className="space-y-2">
      <Progress value={value} aria-label={label} getValueLabel={() => label} />
      <p className="text-sm">{label}</p>
    </div>
  );
}

/** A loan's amount in its own currency; in another currency than the household's, the converted one in a tooltip. */
function Amount({ row, amount, converted }: { row: LoanOverviewRow; amount: string; converted?: MoneyValue | null }) {
  const { t } = useTranslation();
  const own = money(row.currency, amount);
  if (!converted || converted.currency === row.currency) return <span className="tabular-nums">{own}</span>;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span tabIndex={0} className="tabular-nums underline decoration-dotted underline-offset-4">{own}</span>
      </TooltipTrigger>
      <TooltipContent>{t('loans_overview.converted', { amount: formatMoney(converted) })}</TooltipContent>
    </Tooltip>
  );
}

function Running({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const { rankOf } = useLoanNames(d);
  const figure = useFigure(d);
  const dash = <span aria-hidden="true">{t('investments.dash')}</span>;
  const name = (r: LoanOverviewRow) => (
    <span className="inline-flex items-start gap-2">
      <span aria-hidden="true" className="mt-1.5 size-2.5 shrink-0 rounded-full" style={{ background: loanColor(rankOf(r.id)) }} />
      <span className="inline-flex flex-col gap-0.5">
        <span className="font-medium">{r.name}</span>
        {r.lender || r.propertyName ? <span className="text-sm text-muted-foreground">{[r.lender, r.propertyName].filter(Boolean).join(' · ')}</span> : null}
      </span>
    </span>
  );
  const progress = (r: LoanOverviewRow) => (
    <span className="inline-flex items-center gap-2">
      <span aria-hidden="true" className="h-1.5 w-16 rounded-full bg-muted">
        <span className="block h-full rounded-full bg-primary" style={{ width: `${Math.min(100, toPlot(r.repaidShare) * 100)}%` }} />
      </span>
      <span className="text-sm tabular-nums">{percent(t, r.repaidShare)}</span>
    </span>
  );
  return (
    <div className="space-y-2">
      <SortableTable<LoanOverviewRow> caption={t('loans_overview.running_list')} rows={d.loans} rowKey={(r) => r.id}
        rowLink={(r) => ({ to: '/loans/$id', params: { id: r.id } })}
        columns={[
          { id: 'name', header: t('loans_overview.col_loan'), sort: (r) => r.name, cell: name },
          { id: 'owner', header: t('loans_overview.col_owners'), cell: (r) => <OwnerChips owners={r.owners} /> },
          { id: 'progress', header: t('loans_overview.col_progress'), sort: (r) => amountKey(r.repaidShare), cell: progress },
          { id: 'balance', header: t('loans_overview.col_balance'), align: 'end', sort: (r) => amountKey(r.balanceConverted?.amount ?? null),
            cell: (r) => <Amount row={r} amount={r.balance} converted={r.balanceConverted} /> },
          { id: 'monthly', header: t('loans_overview.col_monthly'), align: 'end', sort: (r) => amountKey(r.monthlyPaymentConverted?.amount ?? null),
            cell: (r) => (r.monthlyPayment ? <Amount row={r} amount={r.monthlyPayment} converted={r.monthlyPaymentConverted} /> : dash) },
          { id: 'rate', header: t('loans_overview.col_rate'), align: 'end', sort: (r) => amountKey(r.currentRate), cell: (r) => (r.currentRate ? percent(t, r.currentRate) : dash) },
          { id: 'end', header: t('loans_overview.col_end'), sort: (r) => r.endDate ?? '', cell: (r) => (r.endDate ? <LocalDate value={r.endDate} style="short" /> : dash) },
        ]}
        mobile={(r) => (
          <div className="space-y-1">
            <div className="flex items-center justify-between gap-2">{name(r)}<OwnerChips owners={r.owners} /></div>
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <Amount row={r} amount={r.balance} converted={r.balanceConverted} />
              {progress(r)}
            </div>
          </div>
        )} />
      <p className="flex flex-wrap justify-between gap-2 border-t pt-2 text-sm font-medium">
        <span>{t('loans_overview.total_row', { count: d.runningCount })}</span>
        <span className="tabular-nums">
          {figure(d.balance)} · {figure(d.monthlyPayment, (amount) => t('loans_overview.monthly_value', { amount }))}
        </span>
      </p>
    </div>
  );
}

const UPCOMING_SHOWN = 3;

/** Loans dashboard §3.4: the next payment, then the coming events, as the dashboard's agenda (`GroupedList`). */
function Upcoming({ d }: { d: LoansOverview }) {
  const { t } = useTranslation();
  const { nameOf } = useLoanNames(d);
  const amount = (m?: MoneyValue | null) => (m ? formatMoney(m) : t('loans_overview.unknown'));
  const tile = tileOf('/loans');
  return (
    <SectionCard title={t('loans_overview.upcoming')}>
      {!d.nextPaymentOn && d.events.length === 0 ? <p className="text-sm text-muted-foreground">{t('loans_overview.upcoming_none')}</p> : (
        <GroupedList>
          {d.nextPaymentOn ? (
            <GroupedRow link={{ to: '/loans', search: (prev: RootSearch & LoansSearch) => ({ ...prev, tab: 'list' as const }) }} tile={tile}
              title={t('loans_overview.next_payment')} detail={<LocalDate value={d.nextPaymentOn} style="short" />} trailing={amount(d.nextPaymentTotal)} />
          ) : null}
          {d.events.slice(0, UPCOMING_SHOWN).map((e) => (
            <GroupedRow key={`${e.date}/${e.loanId}/${e.code}`} link={{ to: '/loans/$id', params: { id: e.loanId } }} tile={tile}
              title={e.code === 'realestate.phase_ends'
                ? t('loans_overview.event_phase_ends', { n: e.phaseSeq, name: nameOf(e.loanId), amount: amount(e.monthlyPaymentAfter) })
                : t('loans_overview.event_loan_ends', { name: nameOf(e.loanId), amount: amount(e.monthlyPaymentAfter) })}
              detail={<LocalDate value={e.date} style="short" />} />
          ))}
        </GroupedList>
      )}
    </SectionCard>
  );
}
