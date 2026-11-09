import { Link } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from 'react';
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
import { daysIn } from './monthGrid';
import { atEnd, atStart, dayOffset, extend, initialRange, months, type Range } from './timeline';
import { useDragScroll } from './useDragScroll';

type Scope = 'household' | 'me';
type Side = 'past' | 'future';
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

  // Read once per scope: a failed current month (null: the band starts there, its segment says what went wrong) is
  // fetched again when its segment mounts, and must not unmount the band meanwhile.
  const [start, setStart] = useState<{ scope: Scope; first: string | null }>();
  const known = head.data ? (head.data.firstMonth ?? null) : head.error ? null : undefined;
  if (known !== undefined && start?.scope !== scope) setStart({ scope, first: known });

  if (start?.scope !== scope) return <Loading label={t('timeline.loading_view')}><Skeleton className="h-64 w-full" /></Loading>;
  return <Band key={scope} scope={scope} today={today} first={start.first} />;
}

function Band({ scope, today, first }: { scope: Scope; today: string; first: string | null }) {
  const { t } = useTranslation('subscriptions-board');
  const current = today.slice(0, 7);
  const [range, setRange] = useState<Range>(() => initialRange(current, first));
  const band = useRef<HTMLDivElement>(null);
  const before = useRef<number | null>(null); // the band's width before months were added on the left
  useDragScroll(band);

  const grow = useCallback((side: Side) => {
    if (side === 'past' && band.current) before.current = band.current.scrollWidth;
    setRange((r) => extend(r, side, current, first));
  }, [current, first]);

  // Months added on the left push everything right: shift back by their width, so what was on screen stays.
  useLayoutEffect(() => {
    const el = band.current;
    if (!el || before.current === null) return;
    el.scrollLeft += el.scrollWidth - before.current;
    before.current = null;
  }, [range.from]);

  const toToday = useCallback((behavior: ScrollBehavior) => {
    const el = band.current;
    const mark = el?.querySelector('[data-today]');
    if (!el || !mark) return;
    const box = mark.getBoundingClientRect();
    const at = el.scrollLeft + box.left + box.width / 2 - el.getBoundingClientRect().left;
    el.scrollTo({ left: at - el.clientWidth / 2, behavior });
  }, []);
  useLayoutEffect(() => toToday('auto'), [toToday]);

  const step = (by: number) => band.current?.scrollBy({ left: by * (band.current.querySelector<HTMLElement>('[data-month]')?.offsetWidth || 320), behavior: motion() });

  return (
    // A delay: a disc crossed while dragging does not flash its tooltip.
    <TooltipProvider delayDuration={400}>
      <div className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <ul aria-label={t('timeline.legend')} className="flex flex-wrap items-center gap-4 text-sm text-muted-foreground">
            <li className="flex items-center gap-1.5"><span aria-hidden="true" className="size-3 rounded-full bg-muted-foreground" />{t('timeline.legend_paid')}</li>
            <li className="flex items-center gap-1.5"><span aria-hidden="true" className="size-3 rounded-full border-2 border-dashed border-muted-foreground" />{t('timeline.legend_projected')}</li>
            <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-3 w-0.5 bg-primary" />{t('timeline.today')}</li>
          </ul>
          <div className="flex items-center gap-1">
            <Button type="button" variant="ghost" size="icon" aria-label={t('timeline.previous')} onClick={() => step(-1)}><ChevronLeftIcon aria-hidden="true" /></Button>
            <Button type="button" variant="outline" size="sm" onClick={() => toToday(motion())}>{t('timeline.today')}</Button>
            <Button type="button" variant="ghost" size="icon" aria-label={t('timeline.next')} onClick={() => step(1)}><ChevronRightIcon aria-hidden="true" /></Button>
          </div>
        </div>
        <div ref={band} role="region" aria-label={t('timeline.band')} tabIndex={0}
          className="cursor-grab overflow-x-auto rounded-lg border bg-card outline-none select-none focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[dragging]:cursor-grabbing">
          <div className="flex w-max">
            {atStart(range, current, first) ? <EndCap>{t('timeline.start')}</EndCap>
              : <Edge key={range.from} side="past" root={band} onReach={grow} />}
            {months(range).map((m) => <MonthSegment key={m} month={m} scope={scope} today={today} />)}
            {atEnd(range, current) ? <EndCap>{t('timeline.end')}</EndCap>
              : <Edge key={range.to} side="future" root={band} onReach={grow} />}
          </div>
        </div>
      </div>
    </TooltipProvider>
  );
}

const motion = (): ScrollBehavior => (window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth');

/**
 * Room at a loading end of the band: coming within a segment of it asks for more months. Keyed by the range's end, it is
 * a new element after each step, so an end still in view asks again.
 */
function Edge({ side, root, onReach }: { side: Side; root: RefObject<HTMLDivElement | null>; onReach: (side: Side) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) onReach(side);
    }, { root: root.current, rootMargin: '0px 320px' });
    observer.observe(el);
    return () => observer.disconnect();
  }, [side, root, onReach]);
  return <div ref={ref} data-edge={side} aria-hidden="true" className="w-24 shrink-0" />;
}

/** A band's end: what lies beyond. */
function EndCap({ children }: { children: ReactNode }) {
  return (
    <div className="flex w-24 shrink-0 items-center justify-center p-3 text-center text-xs text-muted-foreground">{children}</div>
  );
}

function MonthSegment({ month, scope, today }: { month: string; scope: Scope; today: string }) {
  const { t } = useTranslation('subscriptions-board');
  const { $api } = useApi();
  // A past month does not change on its own: no refetch of every mounted month when the window regains focus.
  const answer = $api.useQuery('get', CHARGES, { params: { query: { scope, month } } }, { refetchOnWindowFocus: month >= today.slice(0, 7) });
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
      {TICKS.filter((day) => day <= daysIn(Number(month.slice(0, 4)), Number(month.slice(5, 7)))).map((day) => {
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
