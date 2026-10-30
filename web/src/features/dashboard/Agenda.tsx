import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { CalendarClock, CalendarX, Flag, Hourglass, KeyRound, Landmark, PiggyBank, RefreshCw, Receipt, Scale, TimerOff, Wallet, type LucideIcon } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { PendingLine } from '../proposals/PendingLine';
import { usePendingCount } from '../proposals/api';
import { categoryText } from '../properties/format';
import { SectionCard } from '@/components/page/SectionCard';
import { todayIso } from '../subscriptions/today';
import type { DashboardEvent, DashboardView } from './api';
import { ItemLink } from './ItemLink';
import { incomeGaps, isRoutineTodo } from './todo';

function EventText({ e }: { e: DashboardEvent }) {
  const { t } = useTranslation();
  switch (e.kind) {
    case 'subscription_renewal': return <>{t('dashboard.upcoming.subscription_renewal')}</>;
    case 'subscription_expiry': return <>{t('dashboard.upcoming.subscription_expiry')}</>;
    case 'subscription_cancel_deadline': return <>{t('dashboard.upcoming.subscription_cancel_deadline')}</>;
    case 'income_phase_end': return <>{t('dashboard.upcoming.income_phase_end', { label: e.label ?? '' })}</>;
    case 'loan_phase_end': return <>{t('dashboard.upcoming.loan_phase_end', { n: e.label ?? '' })}</>;
    case 'loan_end': return <>{t('dashboard.upcoming.loan_end')}</>;
    case 'valuation_due': return <>{t('dashboard.upcoming.valuation_due')}</>;
    case 'property_cost_due': return <>{categoryText(t, e.label)}</>;
    case 'tenancy_end': return <>{t('dashboard.upcoming.tenancy_end', { label: e.label ?? '' })}</>;
    case 'holding_maturity': return <>{t('dashboard.upcoming.holding_maturity', { label: e.label ?? '' })}</>;
    case 'per_deadline': return <>{t('dashboard.upcoming.per_deadline')}</>;
    case 'per_ceiling_expiring': return <>{t('dashboard.upcoming.per_ceiling_expiring')}</>;
    default: return null;
  }
}

const ICON: Record<string, LucideIcon> = {
  subscription_renewal: RefreshCw, subscription_expiry: CalendarX, subscription_cancel_deadline: CalendarClock, income_phase_end: Wallet, loan_phase_end: Landmark,
  loan_end: Flag, valuation_due: Scale, property_cost_due: Receipt, tenancy_end: KeyRound, holding_maturity: Hourglass,
  per_deadline: PiggyBank, per_ceiling_expiring: TimerOff,
};
// The module's colour, as in « Où part le revenu du mois »: subscriptions, loans and property costs take their chart
// colour, the income the accent, the PER deadline the tax's; an icon carries it, the text says what it is.
const TINT: Record<string, string> = {
  subscriptions: 'var(--chart-1)', loans: 'var(--chart-2)', properties: 'var(--chart-3)', income: 'var(--primary)', tax: 'var(--chart-4)',
};

const NAME = 'block truncate text-xs text-muted-foreground hover:text-foreground hover:underline';

/** « Aujourd’hui », « Demain », else the weekday and date, the year only when it is not this one. */
function dayLabel(t: TFunction, on: string, today: string): string {
  if (on === today) return t('dashboard.upcoming.today');
  const [y, m, d] = today.split('-').map(Number) as [number, number, number];
  if (on === new Date(Date.UTC(y, m - 1, d + 1)).toISOString().slice(0, 10)) return t('dashboard.upcoming.tomorrow');
  const [year, month, day] = on.split('-').map(Number) as [number, number, number];
  return new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', weekday: 'long', day: 'numeric', month: 'long', year: year === y ? undefined : 'numeric' })
    .format(new Date(Date.UTC(year, month - 1, day)));
}

/** Spec §4.1: the next 60 days, sorted by the server, date and amount; one group per day. */
export function Upcoming({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const today = todayIso();
  const listId = useId();
  const days = d.upcoming.reduce<{ on: string; events: DashboardEvent[] }[]>((acc, e) => {
    const last = acc.at(-1);
    if (last?.on === e.on) last.events.push(e);
    else acc.push({ on: e.on, events: [e] });
    return acc;
  }, []);
  return (
    <SectionCard title={t('dashboard.upcoming.title')} note={t('dashboard.upcoming.window')} guide="dashboard.upcoming">
      {days.length === 0 ? <p className="text-sm text-muted-foreground">{t('dashboard.upcoming.empty')}</p> : (
        <div className="space-y-4">
          {days.map((day) => (
            <div key={day.on}>
              {/* A label rather than a heading: the card's title is no heading, so an h3 here would skip a level. */}
              <p id={`${listId}-${day.on}`} className="mb-1 text-xs font-medium text-muted-foreground first-letter:uppercase">
                <time dateTime={day.on} title={formatLocalDate(day.on)}>{dayLabel(t, day.on, today)}</time>
              </p>
              <ul aria-labelledby={`${listId}-${day.on}`} className="divide-y text-sm">
                {day.events.map((e) => {
                  const Icon = ICON[e.kind] ?? CalendarClock;
                  const tint = TINT[e.module] ?? 'var(--muted-foreground)';
                  return (
                    <li key={`${e.kind}-${e.id ?? e.name}-${e.on}-${e.label ?? ''}`} className="flex items-center gap-3 py-2">
                      <span aria-hidden="true" className="flex size-8 shrink-0 items-center justify-center rounded-lg"
                        style={{ color: tint, background: `color-mix(in oklab, ${tint} 14%, transparent)` }}>
                        <Icon className="size-4" />
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate"><EventText e={e} /></span>
                        {e.id ? <ItemLink module={e.module} id={e.id} className={NAME}>{e.name}</ItemLink> : (
                          // The PER deadline is about a person (or the household), not an item: it leads to the tax page's PER block.
                          <Link to="/tax" className={NAME}>{e.label === 'household' ? t('dashboard.upcoming.per_household') : e.name}</Link>
                        )}
                      </span>
                      {e.amount ? <span className="shrink-0 font-medium tabular-nums">{formatMoney(e.amount)}</span> : null}
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </div>
      )}
    </SectionCard>
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
