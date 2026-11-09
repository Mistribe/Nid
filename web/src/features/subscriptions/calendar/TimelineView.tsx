import { Link } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { CHARGES } from '@/api/subscriptions';
import { formatMoney, Money } from '@/components/Money';
import { Loading } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { todayIso } from '@/lib/today';
import { cn } from '@/lib/utils';
import { DayMore, formatDate, type Line } from './charges';
import { atEnd, atStart, dayOffset, initialRange, months, type Range } from './timeline';

type Scope = 'household' | 'me';
const SHOWN = 3;
const TICKS = [1, 8, 15, 22, 29];
const pct = (offset: number) => `${offset * 100}%`;

/**
 * Issue #65: the charges on one horizontal band, a segment per month, from the first charge to twelve months ahead.
 * The current month's answer gives the first charge's month, so the range waits for it; a new scope starts afresh.
 */
export function TimelineView({ scope }: { scope: Scope }) {
  const { t } = useTranslation('subscriptions-board');
  const { $api } = useApi();
  const today = todayIso();
  const current = today.slice(0, 7);
  const head = $api.useQuery('get', CHARGES, { params: { query: { scope, month: current } } });

  if (head.error) return <ProblemMessage problem={present(toProblem(head.error))} />;
  if (!head.data) return <Loading label={t('timeline.loading_view')}><Skeleton className="h-64 w-full" /></Loading>;
  return <Band key={scope} scope={scope} today={today} first={head.data.firstMonth ?? null} />;
}

function Band({ scope, today, first }: { scope: Scope; today: string; first: string | null }) {
  const { t } = useTranslation('subscriptions-board');
  const current = today.slice(0, 7);
  const [range] = useState<Range>(() => initialRange(current, first));
  const band = useRef<HTMLDivElement>(null);
  const step = (by: number) => band.current?.scrollBy({ left: by * (band.current.querySelector<HTMLElement>('[data-month]')?.offsetWidth ?? 320), behavior: 'smooth' });
  const toToday = () => band.current?.querySelector('[data-today]')?.scrollIntoView({ inline: 'center', block: 'nearest', behavior: 'smooth' });

  return (
    <TooltipProvider>
      <div className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <ul aria-label={t('timeline.legend')} className="flex flex-wrap items-center gap-4 text-sm text-muted-foreground">
            <li className="flex items-center gap-1.5"><span aria-hidden="true" className="size-3 rounded-full bg-muted-foreground" />{t('timeline.legend_paid')}</li>
            <li className="flex items-center gap-1.5"><span aria-hidden="true" className="size-3 rounded-full border-2 border-dashed border-muted-foreground" />{t('timeline.legend_projected')}</li>
            <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-3 w-0.5 bg-primary" />{t('timeline.today')}</li>
          </ul>
          <div className="flex items-center gap-1">
            <Button type="button" variant="ghost" size="icon" aria-label={t('timeline.previous')} onClick={() => step(-1)}><ChevronLeftIcon aria-hidden="true" /></Button>
            <Button type="button" variant="outline" size="sm" onClick={toToday}>{t('timeline.today')}</Button>
            <Button type="button" variant="ghost" size="icon" aria-label={t('timeline.next')} onClick={() => step(1)}><ChevronRightIcon aria-hidden="true" /></Button>
          </div>
        </div>
        <div ref={band} role="region" aria-label={t('timeline.band')} tabIndex={0}
          className="overflow-x-auto rounded-lg border bg-card outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">
          <div className="flex w-max">
            {atStart(range, current, first) ? <EndCap>{t('timeline.start')}</EndCap> : <EndCap />}
            {months(range).map((m) => <MonthSegment key={m} month={m} scope={scope} today={today} />)}
            {atEnd(range, current) ? <EndCap>{t('timeline.end')}</EndCap> : <EndCap />}
          </div>
        </div>
      </div>
    </TooltipProvider>
  );
}

/** A band's end: what lies beyond, or room for the next months while they load. */
function EndCap({ children }: { children?: ReactNode }) {
  return (
    <div className="flex w-24 shrink-0 items-center justify-center p-3 text-center text-xs text-muted-foreground">{children}</div>
  );
}

function MonthSegment({ month, scope, today }: { month: string; scope: Scope; today: string }) {
  const { t } = useTranslation('subscriptions-board');
  const { $api } = useApi();
  const answer = $api.useQuery('get', CHARGES, { params: { query: { scope, month } } });
  const label = formatDate(month, { month: 'long', year: 'numeric' });
  const d = answer.data;

  return (
    <div role="group" aria-label={label} data-month={month} className="w-[85vw] max-w-80 shrink-0 border-l md:w-80">
      <div className="flex items-baseline justify-between gap-2 px-3 pt-3">
        <h2 className="text-sm font-semibold first-letter:uppercase">{label}</h2>
        {d ? <span className="text-xs text-muted-foreground tabular-nums">
          {t('timeline.summary', { count: d.charges.length, total: d.total ? formatMoney(d.total) : '—' })}
        </span> : null}
      </div>
      {answer.error ? <div className="p-3"><ProblemMessage problem={present(toProblem(answer.error))} /></div>
        : !d ? (
          <Loading label={t('timeline.loading', { month: label })} className="p-3">
            <div className="flex h-40 items-center justify-center rounded-md bg-muted text-xs text-muted-foreground">{t('timeline.loading', { month: label })}</div>
          </Loading>
        ) : <Plot month={month} lines={d.charges} today={today} />}
    </div>
  );
}

/** One month's axis: the past shaded, today's line, each day's discs stacked upward from the axis. */
function Plot({ month, lines, today }: { month: string; lines: Line[]; today: string }) {
  const { t } = useTranslation('subscriptions-board');
  const current = today.slice(0, 7);
  const byDay = new Map<string, Line[]>();
  for (const c of lines) byDay.set(c.date, [...(byDay.get(c.date) ?? []), c]);
  const past = month < current ? 1 : month === current ? dayOffset(today) : 0;

  return (
    <div className="relative mx-5 h-48">
      {past > 0 ? <div aria-hidden="true" className="absolute top-0 bottom-6 -left-5 bg-muted/60" style={{ width: `calc(${pct(past)} + 1.25rem)` }} /> : null}
      <div aria-hidden="true" className="absolute -inset-x-5 bottom-6 h-px bg-border" />
      {TICKS.map((day) => {
        const at = pct(dayOffset(`${month}-${String(day).padStart(2, '0')}`));
        return (
          <div key={day} aria-hidden="true">
            <div className="absolute bottom-5 h-1 w-px bg-border" style={{ left: at }} />
            <div className="absolute bottom-0 w-6 -translate-x-1/2 text-center text-xs text-muted-foreground" style={{ left: at }}>{day}</div>
          </div>
        );
      })}
      {month === current ? (
        <div data-today="" className="absolute top-0 bottom-6 flex -translate-x-1/2 flex-col items-center" style={{ left: pct(dayOffset(today)) }}>
          <span className="rounded-full bg-primary px-2 py-0.5 text-xs font-medium text-primary-foreground">{t('timeline.today')}</span>
          <span aria-hidden="true" className="w-0.5 flex-1 bg-primary" />
        </div>
      ) : null}
      {[...byDay].map(([date, day]) => {
        const shown = day.length > SHOWN ? day.slice(0, SHOWN - 1) : day;
        const label = formatDate(date, { weekday: 'long', day: 'numeric', month: 'long' });
        return (
          <div key={date} className="absolute bottom-8 flex -translate-x-1/2 flex-col-reverse items-center gap-1" style={{ left: pct(dayOffset(date)) }}>
            {shown.map((c, i) => <Disc key={`${c.itemId}/${i}`} c={c} />)}
            {day.length > SHOWN ? (
              <DayMore lines={day} label={label} className="size-7 rounded-full border bg-muted px-0"
                more={t('calendar.more', { count: day.length - shown.length })}
                moreLabel={t('calendar.more_label', { count: day.length - shown.length })} />
            ) : null}
          </div>
        );
      })}
    </div>
  );
}

/** A charge: the provider's icon, filled when paid, dashed when forecast; the name says which, the tooltip adds the amount. */
function Disc({ c }: { c: Line }) {
  const { t } = useTranslation('subscriptions-board');
  const amount = formatMoney(c.converted ?? c.price);
  const date = formatDate(c.date, { weekday: 'short', day: 'numeric', month: 'short' });
  const state = t(c.isProjected ? 'timeline.projected' : 'timeline.paid');
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Link to="/subscriptions/$id" params={{ id: c.itemId }} aria-label={t('timeline.marker', { name: c.displayName, amount, state, date })}
          className={cn('flex size-7 items-center justify-center overflow-hidden rounded-full border-2 bg-card outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
            c.isProjected ? 'border-dashed border-muted-foreground' : 'border-transparent')}>
          <ProviderIcon name={c.provider?.name ?? c.displayName} iconUrl={c.provider?.iconUrl} color={c.provider?.labels[0]?.color}
            className={cn('size-full rounded-full', c.isProjected && 'opacity-60')} />
        </Link>
      </TooltipTrigger>
      <TooltipContent>
        <span className="font-medium">{c.displayName}</span> · <Money value={c.converted ?? c.price} tabular />
        <span className="block first-letter:uppercase">{state} · {date}</span>
      </TooltipContent>
    </Tooltip>
  );
}
