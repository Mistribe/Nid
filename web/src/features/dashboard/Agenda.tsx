import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { PendingLine } from '../proposals/PendingLine';
import { usePendingCount } from '@/api/proposals';
import { useExpectedDocuments } from '../documents/api';
import { SectionCard } from '@/components/page/SectionCard';
import { Button } from '@/components/ui/button';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { placeOf, tileOf } from '@/components/nav';
import { todayIso } from '@/lib/today';
import type { DashboardEvent, DashboardView } from '@/api/dashboard';
import { EventText, eventDetail, eventIcon, eventLink } from '../calendar/events';
import { incomeGaps, isRoutineTodo } from './todo';

/** « Aujourd’hui », « Demain », else the weekday and date, the year only when it is not this one. */
function dayLabel(t: TFunction, on: string, today: string): string {
  if (on === today) return t('dashboard.upcoming.today');
  const [y, m, d] = today.split('-').map(Number) as [number, number, number];
  if (on === new Date(Date.UTC(y, m - 1, d + 1)).toISOString().slice(0, 10)) return t('dashboard.upcoming.tomorrow');
  const [year, month, day] = on.split('-').map(Number) as [number, number, number];
  return new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', weekday: 'long', day: 'numeric', month: 'long', year: year === y ? undefined : 'numeric' })
    .format(new Date(Date.UTC(year, month - 1, day)));
}

// Beside the net worth curve on a desktop: a few rows keep the card about the curve's height (issue 391).
const UPCOMING_SHOWN = 3;

/** Spec §4.1: the next 60 days, sorted by the server, date and amount; one group per day, the first few then « Tout voir ». */
export function Upcoming({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const today = todayIso();
  const listId = useId();
  const [all, setAll] = useState(false);
  const shown = all ? d.upcoming : d.upcoming.slice(0, UPCOMING_SHOWN);
  const days = shown.reduce<{ on: string; events: DashboardEvent[] }[]>((acc, e) => {
    const last = acc.at(-1);
    if (last?.on === e.on) last.events.push(e);
    else acc.push({ on: e.on, events: [e] });
    return acc;
  }, []);
  return (
    <SectionCard title={t('dashboard.upcoming.title')} note={t('dashboard.upcoming.window')} guide="dashboard.upcoming"
      actions={<Link to="/calendar" className="text-sm underline">{t('dashboard.upcoming.see_calendar')}</Link>}>
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
                      link={eventLink(e)}
                      tile={{ icon: eventIcon(e), tile: place ? tileOf(place).tile : 'neutral' }}
                      title={<EventText e={e} />}
                      detail={eventDetail(t, e)}
                      trailing={e.amount ? formatMoney(e.amount) : undefined} />
                  );
                })}
              </GroupedList>
            </div>
          ))}
          {d.upcoming.length > UPCOMING_SHOWN ? (
            <Button type="button" variant="link" className="h-auto p-0" onClick={() => setAll((v) => !v)}>
              {all ? t('dashboard.upcoming.less') : t('dashboard.upcoming.all', { count: d.upcoming.length })}
            </Button>
          ) : null}
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
