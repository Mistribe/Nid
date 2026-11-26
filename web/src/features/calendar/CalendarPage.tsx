import { useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { lazy, Suspense, useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { CalendarSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { PageHeader } from '@/components/page/PageHeader';
import { Loading } from '@/components/page/skeletons';
import { StatLine } from '@/components/page/StatLine';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import type { RootSearch } from '@/lib/search';
import { todayIso } from '@/lib/today';
import type { DashboardEvent } from '@/api/dashboard';
import { ChargeLink, DayTotals, FigureText, type CalendarCharge } from './amounts';
import { EventLink } from './EventLink';
import { AMOUNT_MODULES, CALENDAR_MODULES, moduleLabelKey, type AmountModule, type CalendarModule } from './modules';
import { MonthGrid } from './MonthGrid';
import { daysIn, formatDate, shiftMonth } from './calendarDates';
import '@/locales/calendar';

export const CALENDAR_EVENTS = '/api/v1/calendar' as const;

// Its own chunk: the month view does not load it (#240).
const TimelineView = lazy(() => import('./TimelineView').then((m) => ({ default: m.TimelineView })));

/**
 * Issue #239: every module's dated events over a month, from the current one on, filtered by module on the server. Issue #240: or
 * their milestones over years, on a timeline (`?view=timeline`). Issue #241: the month's expected money in and out (`?amounts=1`),
 * with childcare among the modules then.
 */
export function CalendarPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const search: RootSearch & CalendarSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const amounts = search.amounts === 1 && search.view !== 'timeline';
  const shown: readonly AmountModule[] = amounts ? AMOUNT_MODULES : CALENDAR_MODULES;
  // The address keeps childcare's choice while the amounts are off, so it comes back as it was.
  const checked: readonly AmountModule[] = search.modules?.split(',').filter((m): m is AmountModule => AMOUNT_MODULES.includes(m as AmountModule)) ?? AMOUNT_MODULES;
  const listed = shown.filter((m) => checked.includes(m));
  const modules = listed.length > 0 ? listed : shown;
  // None asked is every module: the server's default, so the request stays as it was without amounts.
  const chosen = modules.length === shown.length ? undefined : modules.join(',');
  const modulesId = useId();

  const toggle = (module: AmountModule, on: boolean) => {
    const next = AMOUNT_MODULES.filter((m) => (m === module ? on : m === 'childcare' && !amounts ? checked.includes(m) : modules.includes(m)));
    void navigate({ to: '.', replace: true, resetScroll: false,
      search: (prev) => ({ ...prev, modules: next.length === AMOUNT_MODULES.length ? undefined : next.join(',') }) });
  };

  return (
    <div className="space-y-4">
      <PageHeader title={t('calendar_page.title')} />
      <div className="space-y-2">
        <p id={modulesId} className="text-sm font-medium">{t('calendar_page.modules')}</p>
        <div role="group" aria-labelledby={modulesId} className="flex flex-wrap gap-x-4 gap-y-2">
          {shown.map((m) => {
            const on = modules.includes(m);
            return (
              <div key={m} className="flex items-center gap-2">
                {/* The last module checked stays: an empty calendar would be a filter's doing, not the household's. */}
                <Checkbox id={`${modulesId}-${m}`} checked={on} disabled={on && modules.length === 1}
                  onCheckedChange={(checked) => toggle(m, checked === true)} />
                <Label htmlFor={`${modulesId}-${m}`}>{t(moduleLabelKey(m))}</Label>
              </div>
            );
          })}
        </div>
      </div>
      <Segmented aria-label={t('calendar_page.view')} value={search.view ?? 'month'} options={[
        { value: 'month', label: t('calendar_page.view_month') }, { value: 'timeline', label: t('calendar_page.view_timeline') },
      ]} onValueChange={(v) => void navigate({ to: '.', replace: true, resetScroll: false,
        search: (prev) => ({ ...prev, view: v === 'timeline' ? 'timeline' : undefined }) })} />
      {search.view === 'timeline'
        ? <Suspense fallback={<Loading label={t('calendar_page.loading')}><Skeleton className="h-72 w-full" /></Loading>}>
          <TimelineView scope={scope} modules={modules as readonly CalendarModule[]} chosen={chosen} horizon={search.horizon ?? 5} />
        </Suspense>
        : <MonthView scope={scope} chosen={chosen} month={search.month} amounts={amounts} />}
    </div>
  );
}

type Entry = { on: string; event: DashboardEvent } | { on: string; charge: CalendarCharge };

/** A month of events: previous and next month, a grid (desktop) or a list of days (phone); with its amounts, the money too. */
function MonthView({ scope, chosen, month: asked, amounts }: {
  scope: 'household' | 'me'; chosen: string | undefined; month: string | undefined; amounts: boolean;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const today = todayIso();
  const current = today.slice(0, 7);
  // The server reads no month before the current one.
  const month = asked && asked > current ? asked : current;
  const from = `${month}-01`;
  const to = `${month}-${daysIn(Number(month.slice(0, 4)), Number(month.slice(5, 7)))}`;
  // #756: the month is blanked while it is read again (below), so not each time the tab comes back; a visit or a new choice reads it.
  const answer = $api.useQuery('get', CALENDAR_EVENTS, {
    params: { query: { scope, from, to, ...(chosen ? { modules: chosen } : {}), ...(amounts ? { amounts: true } : {}) } },
  }, { refetchOnWindowFocus: false });
  const money = amounts ? answer.data?.amounts : undefined;
  // Events and charges under one budget a cell, by day (a stable sort keeps the server's order within one).
  const entries: Entry[] = [
    ...(answer.data?.events ?? []).map((event) => ({ on: event.on, event })),
    ...(money?.charges ?? []).map((charge) => ({ on: charge.on, charge })),
  ].sort((a, b) => a.on.localeCompare(b.on));
  const days = new Map((money?.days ?? []).map((d) => [d.on, d]));

  const go = (by: number) => {
    const next = shiftMonth(month, by);
    void navigate({ to: '.', replace: true, resetScroll: false, search: (prev) => ({ ...prev, month: next === current ? undefined : next }) });
  };

  return (
    <>
      <div className="flex items-center gap-2">
        <Button type="button" variant="ghost" size="icon" aria-label={t('calendar_page.previous')} disabled={month === current} onClick={() => go(-1)}>
          <ChevronLeftIcon aria-hidden="true" />
        </Button>
        <h2 className="min-w-40 text-center text-lg font-semibold first-letter:uppercase">{formatDate(month, { month: 'long', year: 'numeric' })}</h2>
        <Button type="button" variant="ghost" size="icon" aria-label={t('calendar_page.next')} onClick={() => go(1)}>
          <ChevronRightIcon aria-hidden="true" />
        </Button>
        <div className="ml-auto">
          <SwitchField label={t('calendar_page.amounts')} checked={amounts} onCheckedChange={(on) => void navigate({ to: '.', replace: true,
            resetScroll: false, search: (prev) => ({ ...prev, amounts: on ? 1 : undefined }) })} />
        </div>
      </div>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        // #756: a month cached from an earlier visit is not shown while it is read again: events never pop in under the reader's eyes.
        : !answer.data || answer.isFetching ? <Loading label={t('calendar_page.loading')}><Skeleton className="h-96 w-full" /></Loading>
        : (
          <>
            {money ? (
              <StatLine items={[
                { label: t('calendar_page.out'), value: <FigureText figure={money.out} /> },
                { label: t('calendar_page.in'), value: <FigureText figure={money.in} /> },
                { label: t('calendar_page.balance'), value: <FigureText figure={money.balance} />, tip: t('calendar_page.balance_tip') },
              ]} />
            ) : null}
            {entries.length === 0 ? <p className="text-sm text-muted-foreground">{t('calendar_page.empty')}</p> : null}
            <MonthGrid month={month} today={today} items={entries} dateOf={(e) => e.on}
              render={(e, compact) => ('event' in e ? <EventLink e={e.event} compact={compact} /> : <ChargeLink c={e.charge} compact={compact} />)}
              aside={money ? (date) => { const day = days.get(date); return day ? <DayTotals day={day} /> : null; } : undefined}
              labels={{
                grid: t('calendar_page.grid'), byDay: t('calendar_page.by_day'),
                more: (count) => t('calendar_page.more', { count }), moreLabel: (count) => t('calendar_page.more_label', { count }),
              }} />
          </>
        )}
    </>
  );
}
