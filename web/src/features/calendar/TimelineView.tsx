import { Link, useNavigate } from '@tanstack/react-router';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import type { DashboardEvent } from '@/api/dashboard';
import { present, toProblem } from '@/api/errors';
import type { CalendarSearch } from '@/app/router';
import { Segmented } from '@/components/form/Segmented';
import { Loading } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { Skeleton } from '@/components/ui/skeleton';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { EventLink, EventTile } from './EventLink';
import { EventText, eventDetail, eventLink } from './events';
import { moduleLabelKey, type CalendarModule } from './modules';
import { formatDate } from './monthGrid';
import { clusters, years } from './timeline';
import '@/locales/calendar';

export const CALENDAR_TIMELINE = '/api/v1/calendar/timeline' as const;
export const HORIZONS = [1, 3, 5, 10] as const;
export type Horizon = (typeof HORIZONS)[number];

const pct = (at: number) => `${at * 100}%`;
const longDate = (date: string) => formatDate(date, { day: 'numeric', month: 'long', year: 'numeric' });
// The row names' column and the axis share these, so a row reads straight across.
const NAMES = 'w-32 shrink-0 px-3';
const TRACK = 'relative mx-5 flex-1';

/**
 * Issue #240: the milestones of the checked modules from today over the horizon, one row per module, one mark per milestone (marks
 * too close to tell apart share one that opens as a list). The server sends milestones only: recurring kinds never reach it.
 */
export function TimelineView({ scope, modules, chosen, horizon }: {
  scope: 'household' | 'me'; modules: readonly CalendarModule[]; chosen: string | undefined; horizon: Horizon;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const answer = $api.useQuery('get', CALENDAR_TIMELINE, {
    params: { query: { scope, horizon, ...(chosen ? { modules: chosen } : {}) } },
  });

  return (
    <div className="space-y-4">
      <Segmented aria-label={t('calendar_page.horizon')} value={String(horizon)}
        options={HORIZONS.map((h) => ({ value: String(h), label: t('calendar_page.years', { count: h }) }))}
        onValueChange={(v) => void navigate({ to: '.', replace: true, resetScroll: false,
          search: (prev) => ({ ...prev, horizon: v === '5' ? undefined : (Number(v) as NonNullable<CalendarSearch['horizon']>) }) })} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? <Loading label={t('calendar_page.loading')}><Skeleton className="h-72 w-full" /></Loading>
        : <Band from={answer.data.from} to={answer.data.to} events={answer.data.events} modules={modules} />}
    </div>
  );
}

function Band({ from, to, events, modules }: { from: string; to: string; events: DashboardEvent[]; modules: readonly CalendarModule[] }) {
  const { t } = useTranslation();
  const id = useId();
  const ticks = years(from, to);
  const grid = ticks.map((y) => <span key={y.year} aria-hidden="true" className="absolute inset-y-0 w-px bg-border" style={{ left: pct(y.at) }} />);
  return (
    <TooltipProvider delayDuration={200}>
      <div className="space-y-2">
        {events.length === 0 ? <p className="text-sm text-muted-foreground">{t('calendar_page.timeline_empty')}</p> : null}
        {/* Too narrow for the axis on a phone: the band scrolls on its own, the page does not. */}
        <div role="region" aria-label={t('calendar_page.timeline', { from: longDate(from), to: longDate(to) })} tabIndex={0}
          className="overflow-x-auto rounded-lg border bg-card outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">
          <div className="min-w-[48rem]">
            <div aria-hidden="true" className="flex h-8 items-center border-b">
              <div className={NAMES} />
              <div className={`${TRACK} h-full text-xs text-muted-foreground`}>
                <span className="absolute top-1/2 left-0 -translate-y-1/2 font-medium text-primary">{t('calendar_page.today')}</span>
                {ticks.map((y) => (
                  <span key={y.year} className="absolute top-1/2 -translate-x-1/2 -translate-y-1/2 tabular-nums" style={{ left: pct(y.at) }}>{y.year}</span>
                ))}
              </div>
            </div>
            {modules.map((m) => (
              <div key={m} className="flex h-12 items-center border-b last:border-b-0">
                <span id={`${id}-${m}`} className={`${NAMES} truncate text-sm`}>{t(moduleLabelKey(m))}</span>
                <div className={`${TRACK} h-full`}>
                  {grid}
                  <ul aria-labelledby={`${id}-${m}`} className="absolute inset-0">
                    {clusters(events.filter((e) => e.module === m), from, to, (e) => e.on).map((c) => (
                      <li key={c.at} className="absolute top-1/2 -translate-x-1/2 -translate-y-1/2" style={{ left: pct(c.at) }}>
                        {c.items.length === 1 ? <Mark e={c.items[0]!} /> : <Several events={c.items} />}
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </TooltipProvider>
  );
}

/** What a mark says: what happens, to what, on which day. */
function Words({ e }: { e: DashboardEvent }) {
  const { t } = useTranslation();
  return <><EventText e={e} /> · {eventDetail(t, e)} · {longDate(e.on)}</>;
}

const MARK = 'flex size-8 items-center justify-center rounded-full border bg-card outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring/50';

/** A milestone: its icon, linked to its item; its words for a screen reader and in a tooltip. */
function Mark({ e }: { e: DashboardEvent }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Link {...eventLink(e)} className={MARK}>
          <EventTile e={e} />
          <span className="sr-only"><Words e={e} /></span>
        </Link>
      </TooltipTrigger>
      <TooltipContent><Words e={e} /></TooltipContent>
    </Tooltip>
  );
}

/** Milestones too close to tell apart: their count, opening their list. */
function Several({ events }: { events: DashboardEvent[] }) {
  const { t } = useTranslation();
  const label = t('calendar_page.several', { count: events.length, from: longDate(events[0]!.on), to: longDate(events.at(-1)!.on) });
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" variant="outline" size="icon" aria-label={label} className="size-8 rounded-full text-xs font-semibold tabular-nums">
          {events.length}
        </Button>
      </PopoverTrigger>
      <PopoverContent aria-label={label} className="max-h-[min(20rem,var(--radix-popover-content-available-height))] w-80 space-y-2 overflow-y-auto p-2">
        {events.map((e, i) => (
          <div key={i}>
            <p className="px-1 text-xs text-muted-foreground">{longDate(e.on)}</p>
            <EventLink e={e} compact={false} />
          </div>
        ))}
      </PopoverContent>
    </Popover>
  );
}
