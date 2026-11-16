import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import type { DashboardEvent } from '@/api/dashboard';
import { present, toProblem } from '@/api/errors';
import type { CalendarSearch } from '@/app/router';
import { ModuleTile } from '@/components/ModuleTile';
import { placeOf, tileOf } from '@/components/nav';
import { PageHeader } from '@/components/page/PageHeader';
import { Loading } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import type { RootSearch } from '@/lib/search';
import { todayIso } from '@/lib/today';
import { cn } from '@/lib/utils';
import { EventText, eventDetail, eventIcon, eventLink } from './events';
import { CALENDAR_MODULES, moduleLabelKey, type CalendarModule } from './modules';
import { MonthGrid } from './MonthGrid';
import { daysIn, formatDate, shiftMonth } from './monthGrid';
import '@/locales/calendar';

export const CALENDAR_EVENTS = '/api/v1/calendar' as const;

/** Issue #239: every module's dated events over a month, from the current one on, filtered by module on the server. */
export function CalendarPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: RootSearch & CalendarSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const today = todayIso();
  const current = today.slice(0, 7);
  // The server reads no month before the current one.
  const month = search.month && search.month > current ? search.month : current;
  const modules: readonly CalendarModule[] = search.modules?.split(',').filter((m): m is CalendarModule => CALENDAR_MODULES.includes(m as CalendarModule)) ?? CALENDAR_MODULES;
  const from = `${month}-01`;
  const to = `${month}-${daysIn(Number(month.slice(0, 4)), Number(month.slice(5, 7)))}`;
  const answer = $api.useQuery('get', CALENDAR_EVENTS, {
    params: { query: { scope, from, to, ...(search.modules ? { modules: search.modules } : {}) } },
  });
  const modulesId = useId();

  const go = (by: number) => {
    const next = shiftMonth(month, by);
    void navigate({ to: '.', replace: true, resetScroll: false, search: (prev: CalendarSearch) => ({ ...prev, month: next === current ? undefined : next }) });
  };
  const toggle = (module: CalendarModule, on: boolean) => {
    const next = CALENDAR_MODULES.filter((m) => (m === module ? on : modules.includes(m)));
    void navigate({ to: '.', replace: true, resetScroll: false,
      search: (prev: CalendarSearch) => ({ ...prev, modules: next.length === CALENDAR_MODULES.length ? undefined : next.join(',') }) });
  };

  return (
    <div className="space-y-4">
      <PageHeader title={t('calendar_page.title')} />
      <div className="space-y-2">
        <p id={modulesId} className="text-sm font-medium">{t('calendar_page.modules')}</p>
        <div role="group" aria-labelledby={modulesId} className="flex flex-wrap gap-x-4 gap-y-2">
          {CALENDAR_MODULES.map((m) => {
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
      <div className="flex items-center gap-2">
        <Button type="button" variant="ghost" size="icon" aria-label={t('calendar_page.previous')} disabled={month === current} onClick={() => go(-1)}>
          <ChevronLeftIcon aria-hidden="true" />
        </Button>
        <h2 className="min-w-40 text-center text-lg font-semibold first-letter:uppercase">{formatDate(month, { month: 'long', year: 'numeric' })}</h2>
        <Button type="button" variant="ghost" size="icon" aria-label={t('calendar_page.next')} onClick={() => go(1)}>
          <ChevronRightIcon aria-hidden="true" />
        </Button>
      </div>
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? <Loading label={t('calendar_page.loading')}><Skeleton className="h-96 w-full" /></Loading>
        : (
          <>
            {answer.data.events.length === 0 ? <p className="text-sm text-muted-foreground">{t('calendar_page.empty')}</p> : null}
            <MonthGrid month={month} today={today} items={answer.data.events} dateOf={(e) => e.on}
              render={(e, compact) => <EventLink e={e} compact={compact} />}
              labels={{
                grid: t('calendar_page.grid'), byDay: t('calendar_page.by_day'),
                more: (count) => t('calendar_page.more', { count }), moreLabel: (count) => t('calendar_page.more_label', { count }),
              }} />
          </>
        )}
    </div>
  );
}

/** An event, with « À venir »'s icon and words, linked to its item (or to the tax page). */
function EventLink({ e, compact }: { e: DashboardEvent; compact: boolean }) {
  const { t } = useTranslation();
  const place = placeOf(e.module);
  const detail = eventDetail(t, e);
  return (
    <Link {...eventLink(e)} title={compact ? detail : undefined}
      className={cn('flex min-w-0 items-center gap-2 rounded-sm px-1 hover:bg-accent', compact ? 'text-xs' : 'py-1 text-sm')}>
      <ModuleTile icon={eventIcon(e)} tile={place ? tileOf(place).tile : 'neutral'} />
      <span className="min-w-0 flex-1 truncate">
        <span className="font-medium"><EventText e={e} /></span>
        <span className="text-muted-foreground"> · {detail}</span>
      </span>
    </Link>
  );
}
