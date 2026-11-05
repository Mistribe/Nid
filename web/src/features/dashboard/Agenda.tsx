import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { CalendarClock, CalendarX, FileText, Flag, Hourglass, KeyRound, Landmark, Milestone, Percent, PiggyBank, RefreshCw, Receipt, Scale, TimerOff, Wallet, type LucideIcon } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { PendingLine } from '../proposals/PendingLine';
import { usePendingCount } from '../proposals/api';
import { useExpectedDocuments } from '../documents/api';
import { categoryText } from '../properties/format';
import { SectionCard } from '@/components/page/SectionCard';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { placeOf, tileOf } from '@/app/nav';
import { todayIso } from '../subscriptions/today';
import type { DashboardEvent, DashboardView } from './api';
import { itemRoute } from './ItemLink';
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
    case 'tax_milestone': {
      const milestone = (['life_insurance_8y', 'pea_5y', 'per_retirement'] as const).find((m) => m === e.label);
      return milestone ? <>{t(`dashboard.upcoming.tax_milestone.${milestone}`)}</> : null;
    }
    case 'per_deadline': return <>{t('dashboard.upcoming.per_deadline')}</>;
    case 'per_ceiling_expiring': return <>{t('dashboard.upcoming.per_ceiling_expiring')}</>;
    case 'tax_filing_deadline': return <>{t('dashboard.upcoming.tax_filing_deadline')}</>;
    case 'tax_new_rate': return <>{t('dashboard.upcoming.tax_new_rate')}</>;
    case 'tax_balance_due': return <>{t('dashboard.upcoming.tax_balance_due')}</>;
    case 'property_tax_due': return <>{t('dashboard.upcoming.property_tax_due')}</>;
    default: return null;
  }
}

const ICON: Record<string, LucideIcon> = {
  subscription_renewal: RefreshCw, subscription_expiry: CalendarX, subscription_cancel_deadline: CalendarClock, income_phase_end: Wallet, loan_phase_end: Landmark,
  loan_end: Flag, valuation_due: Scale, property_cost_due: Receipt, tenancy_end: KeyRound, holding_maturity: Hourglass,
  per_deadline: PiggyBank, per_ceiling_expiring: TimerOff, tax_milestone: Milestone,
  tax_filing_deadline: FileText, tax_new_rate: Percent, tax_balance_due: Landmark, property_tax_due: Receipt,
};
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
              <GroupedList labelledBy={`${listId}-${day.on}`}>
                {day.events.map((e) => {
                  const place = placeOf(e.module);
                  return (
                    <GroupedRow key={`${e.kind}-${e.id ?? e.name}-${e.on}-${e.label ?? ''}`}
                      // The PER deadline and the tax calendar are about a person or the household, not an item: they lead to the tax page.
                      link={e.id ? itemRoute(e.module, e.id) : { to: '/tax' }}
                      tile={{ icon: ICON[e.kind] ?? CalendarClock, tile: place ? tileOf(place).tile : 'neutral' }}
                      title={<EventText e={e} />}
                      detail={e.id ? e.name : e.label === 'household' ? t('dashboard.upcoming.per_household')
                        : e.label === 'department_unknown' ? t('dashboard.upcoming.tax_department_unknown') : e.name}
                      trailing={e.amount ? formatMoney(e.amount) : undefined} />
                  );
                })}
              </GroupedList>
            </div>
          ))}
        </div>
      )}
    </SectionCard>
  );
}

/** Spec §4.1: one line when something is due (the month's routine, 2E), proposals wait (4C2) or documents are expected (#414); nothing otherwise. */
export function Notice({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const pending = usePendingCount() ?? 0;
  const routine = d.todo.filter(isRoutineTodo).length;
  const gaps = incomeGaps(d.todo);
  const expected = useExpectedDocuments().data?.items?.length ?? 0;
  if (routine === 0 && gaps.length === 0 && pending === 0 && expected === 0) return null;
  return (
    <p className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-lg border bg-muted/40 px-3 py-2 text-sm">
      {routine > 0 ? <Link to="/routine" className="underline">{t('dashboard.notice', { count: routine })}</Link> : null}
      {gaps.length === 1 ? <Link to="/income/$id" params={{ id: gaps[0]!.id }} className="underline">{t('dashboard.notice_income_gap', { count: 1 })}</Link> : null}
      {gaps.length > 1 ? <Link to="/income" className="underline">{t('dashboard.notice_income_gap', { count: gaps.length })}</Link> : null}
      {pending > 0 ? <PendingLine /> : null}
      {expected > 0 ? <Link to="/documents" className="underline">{t('dashboard.notice_expected_documents', { count: expected })}</Link> : null}
    </p>
  );
}
