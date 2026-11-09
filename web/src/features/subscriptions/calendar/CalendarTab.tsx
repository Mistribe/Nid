import { lazy, Suspense } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import i18n from '@/app/i18n';
import type { SubscriptionsSearch } from '@/app/router';
import { formatMoney } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import board from '@/locales/fr.subscriptions-board.json';
import { cn } from '@/lib/utils';
import { CHARGES } from '@/api/subscriptions';
import { ChargeLink, DayMore, formatDate as format, type Line } from './charges';
import { todayIso } from '@/lib/today';
import { monthGrid, shiftMonth } from './monthGrid';
import { Loading, StatLineSkeleton } from '@/components/page/skeletons';

// Loaded with this lazy chunk, not with the initial bundle.
i18n.addResourceBundle('fr', 'subscriptions-board', board, true, true);

const TimelineView = lazy(() => import('./TimelineView').then((m) => ({ default: m.TimelineView })));

// 2024-01-01 was a Monday.
const WEEKDAYS = Array.from({ length: 7 }, (_, i) => format(`2024-01-0${i + 1}`, { weekday: 'short' }));
const SHOWN = 3;

/** The calendar tab: the month calendar, or the timeline (issue #65) under `?view=timeline`. */
export function CalendarTab({ scope }: { scope: 'household' | 'me' }) {
  const { t } = useTranslation('subscriptions-board');
  const navigate = useNavigate();
  const search: SubscriptionsSearch = useSearch({ strict: false });
  const view = search.view === 'timeline' ? 'timeline' : 'calendar';
  return (
    <div className="space-y-4">
      <Segmented aria-label={t('view.label')} value={view} options={[
        { value: 'calendar', label: t('view.calendar') }, { value: 'timeline', label: t('view.timeline') },
      ]} onValueChange={(v) => void navigate({ to: '.', replace: true, resetScroll: false,
        search: (prev) => ({ ...prev, view: v === 'timeline' ? 'timeline' : undefined }) })} />
      {view === 'timeline'
        ? <Suspense fallback={<Loading label={t('timeline.loading_view')}><Skeleton className="h-64 w-full" /></Loading>}><TimelineView scope={scope} /></Suspense>
        : <MonthCalendar scope={scope} />}
    </div>
  );
}

/** Subscriptions dashboard design §5.2: one month of the server's charges, as a grid (desktop) or a list of days (phone). */
function MonthCalendar({ scope }: { scope: 'household' | 'me' }) {
  const { t } = useTranslation('subscriptions-board');
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: SubscriptionsSearch = useSearch({ strict: false });
  const today = todayIso();
  const current = today.slice(0, 7);
  const month = search.month ?? current;
  // No previous month kept on screen: its charges would land on the new grid.
  const answer = $api.useQuery('get', CHARGES, { params: { query: { scope, month } } });

  const go = (by: number) => {
    const next = shiftMonth(month, by);
    void navigate({ to: '.', replace: true, resetScroll: false, search: (prev) => ({ ...prev, month: next === current ? undefined : next }) });
  };

  const header = (
    <div className="flex items-center gap-2">
      <Button type="button" variant="ghost" size="icon" aria-label={t('calendar.previous')} onClick={() => go(-1)}><ChevronLeftIcon aria-hidden="true" /></Button>
      <h2 className="min-w-40 text-center text-lg font-semibold first-letter:uppercase">{format(month, { month: 'long', year: 'numeric' })}</h2>
      <Button type="button" variant="ghost" size="icon" aria-label={t('calendar.next')} onClick={() => go(1)}><ChevronRightIcon aria-hidden="true" /></Button>
    </div>
  );

  // The header keeps its place while the month loads: a click on « › » is not lost to a remount.
  if (answer.error) return <div className="space-y-4">{header}<ProblemMessage problem={present(toProblem(answer.error))} /></div>;
  const d = answer.data;
  if (!d) return <div className="space-y-4">{header}<Loading label={t('calendar.loading')}><StatLineSkeleton items={3} /><Skeleton className="h-96 w-full" /></Loading></div>;

  const byDay = new Map<string, Line[]>();
  for (const c of d.charges) byDay.set(c.date, [...(byDay.get(c.date) ?? []), c]);
  const horizon = d.findings.some((f) => f.code === 'subscriptions.horizon_reached');
  const beyond = horizon && d.charges.length === 0; // with charges, the forecast stops within the month

  return (
    <div className="space-y-4">
      {header}
      <StatLine items={[
        { label: t('calendar.total'), value: d.total && !beyond ? formatMoney(d.total) : '—' },
        { label: t('calendar.count'), value: d.charges.length },
      ]} />
      {beyond ? <p className="text-sm text-muted-foreground">{t('calendar.beyond')}</p>
        : horizon ? <p className="text-sm text-muted-foreground">{t('calendar.truncated')}</p>
        : d.charges.length === 0 ? <p className="text-sm text-muted-foreground">{t('calendar.empty')}</p> : null}

      <div role="group" aria-label={t('calendar.grid')} className="hidden overflow-hidden rounded-lg border md:grid md:grid-cols-7">
        {WEEKDAYS.map((w) => <div key={w} aria-hidden="true" className="px-2 py-1 text-xs text-muted-foreground">{w}</div>)}
        {monthGrid(month).map((cell) => {
          const lines = byDay.get(cell.date) ?? [];
          const shown = lines.length > SHOWN ? lines.slice(0, SHOWN - 1) : lines;
          const label = format(cell.date, { weekday: 'long', day: 'numeric', month: 'long' });
          return (
            <div key={cell.date} role="group" aria-label={label} aria-current={cell.date === today ? 'date' : undefined}
              className={cn('min-h-24 space-y-1 border-t border-l p-1 [&:nth-child(7n+1)]:border-l-0', !cell.inMonth && 'bg-muted/40 text-muted-foreground')}>
              <div aria-hidden="true" className={cn('inline-flex size-6 items-center justify-center rounded-full text-xs',
                cell.date === today && 'bg-primary font-semibold text-primary-foreground')}>{Number(cell.date.slice(8))}</div>
              {shown.map((c, i) => <ChargeLink key={`${c.itemId}/${i}`} c={c} compact />)}
              {lines.length > SHOWN ? (
                <DayMore lines={lines} label={label} more={t('calendar.more', { count: lines.length - shown.length })}
                  moreLabel={t('calendar.more_label', { count: lines.length - shown.length })} />
              ) : null}
            </div>
          );
        })}
      </div>

      {byDay.size > 0 ? <ul aria-label={t('calendar.by_day')} className="space-y-4 md:hidden">
        {[...byDay].map(([date, lines]) => (
          <li key={date} className="space-y-1">
            <h3 className="text-sm font-semibold">{format(date, { weekday: 'short', day: 'numeric', month: 'short' })}</h3>
            {lines.map((c, i) => <ChargeLink key={`${c.itemId}/${i}`} c={c} />)}
          </li>
        ))}
      </ul> : null}
    </div>
  );
}
