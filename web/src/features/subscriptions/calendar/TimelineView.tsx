import { useQueries } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { CHARGES, type Charges } from '@/api/subscriptions';
import { formatMoney, Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { Loading } from '@/components/page/skeletons';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { toPlot } from '@/lib/plot';
import { todayIso } from '@/lib/today';
import { cn } from '@/lib/utils';
import { byDay, DayList, DayMore, formatDate, type Line } from './charges';
import { daysIn } from './monthGrid';
import { atEnd, atStart, dayOffset, extend, initialRange, lanes as lanesOf, months, type Range } from './timeline';
import { useDragScroll } from './useDragScroll';

type Scope = 'household' | 'me';
type Side = 'past' | 'future';
const TICKS = [1, 8, 15, 22, 29];
const pct = (offset: number) => `${offset * 100}%`;
/** A month's answer, as the band's queries hold it. */
type Answer = { data?: Charges; error: unknown };
// The lane names' column and the months share these heights, so a lane reads straight across.
const HEAD = 'h-16';
const STRIP = 'h-6';
const LANE = 'h-10 border-b border-border/50';
const NAMES = 'w-12 md:w-44';
const AFTER_NAMES = 'left-12 md:left-44';

/**
 * Issues #65 and #89: the charges on one horizontal band, a segment per month from the first charge to twelve months
 * ahead, a lane per subscription; under it, the month in focus. The current month's answer gives the first charge's
 * month, so the range waits for it; a new scope starts afresh.
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
  const { $api } = useApi();
  const current = today.slice(0, 7);
  const [range, setRange] = useState<Range>(() => initialRange(current, first));
  const [focus, setFocus] = useState(current);
  const [ends, setEnds] = useState({ back: false, forward: false });
  const band = useRef<HTMLDivElement>(null);
  const before = useRef<number | null>(null); // the band's width before months were added on the left
  useDragScroll(band);

  const shown = months(range);
  // A past month does not change on its own: no refetch of every mounted month when the window regains focus.
  const answers = useQueries({
    queries: shown.map((month) => $api.queryOptions('get', CHARGES, { params: { query: { scope, month } } }, { refetchOnWindowFocus: month >= current })),
  });
  const loaded = answers.flatMap((a) => (a.data ? [a.data] : []));
  const lanes = lanesOf(loaded);
  const largest = Math.max(0, ...loaded.map((d) => (d.total ? toPlot(d.total.amount) : 0)));

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

  // The edge arrows show only where the band can still move.
  const measure = useCallback(() => {
    const el = band.current;
    if (!el) return;
    const back = el.scrollLeft > 1;
    const forward = el.scrollLeft + el.clientWidth < el.scrollWidth - 1;
    setEnds((e) => (e.back === back && e.forward === forward ? e : { back, forward }));
  }, []);
  useLayoutEffect(measure, [measure, range]);
  useEffect(() => {
    const el = band.current;
    el?.addEventListener('scroll', measure, { passive: true });
    window.addEventListener('resize', measure);
    return () => { el?.removeEventListener('scroll', measure); window.removeEventListener('resize', measure); };
  }, [measure]);

  // The month under the band's centre is the one in focus; a click on a month's name focuses it too.
  useEffect(() => {
    const el = band.current;
    if (!el || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver((entries) => {
      const hit = entries.find((e) => e.isIntersecting);
      if (hit) setFocus((hit.target as HTMLElement).dataset.month!);
    }, { root: el, rootMargin: '0px -50%' });
    el.querySelectorAll('[data-month]').forEach((m) => observer.observe(m));
    return () => observer.disconnect();
  }, [range]);

  const step = (by: number) => band.current?.scrollBy({ left: by * (band.current.querySelector<HTMLElement>('[data-month]')?.offsetWidth || 320), behavior: motion() });
  const focused = answers[shown.indexOf(focus)];

  return (
    // A delay: a disc crossed while dragging does not flash its tooltip.
    <TooltipProvider delayDuration={400}>
      <div className="space-y-6">
        <div className="space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <ul aria-label={t('timeline.legend')} className="flex flex-wrap items-center gap-4 text-sm text-muted-foreground">
              <li className="flex items-center gap-1.5"><span aria-hidden="true" className="size-3 rounded-full bg-muted-foreground" />{t('timeline.legend_paid')}</li>
              <li className="flex items-center gap-1.5"><span aria-hidden="true" className="size-3 rounded-full border-2 border-dashed border-muted-foreground" />{t('timeline.legend_projected')}</li>
              <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-3 w-0.5 bg-primary" />{t('timeline.today')}</li>
              <li className="flex items-center gap-1.5"><span aria-hidden="true" className="h-1.5 w-4 rounded-full bg-chart-1" />{t('timeline.legend_weight')}</li>
            </ul>
            <Button type="button" variant="outline" size="sm" onClick={() => toToday(motion())}>{t('timeline.today')}</Button>
          </div>
          <div className="relative">
            <div ref={band} role="region" aria-label={t('timeline.band')} tabIndex={0}
              className="cursor-grab overflow-x-auto rounded-lg border bg-card outline-none select-none [scrollbar-width:none] focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[dragging]:cursor-grabbing [&::-webkit-scrollbar]:hidden">
              <div className="flex w-max">
                <Names lanes={lanes} />
                {atStart(range, current, first) ? <EndCap>{t('timeline.start')}</EndCap>
                  : <Edge key={range.from} side="past" root={band} onReach={grow} />}
                {shown.map((m, i) => <MonthSegment key={m} month={m} answer={answers[i]!} lanes={lanes} largest={largest}
                  today={today} focused={m === focus} onFocus={() => setFocus(m)} />)}
                {atEnd(range, current) ? <EndCap>{t('timeline.end')}</EndCap>
                  : <Edge key={range.to} side="future" root={band} onReach={grow} />}
              </div>
            </div>
            {ends.back ? <>
              <div aria-hidden="true" className={cn('pointer-events-none absolute inset-y-px z-20 w-10 bg-linear-to-r from-card to-transparent', AFTER_NAMES)} />
              <EdgeArrow side="back" label={t('timeline.previous')} onClick={() => step(-1)} />
            </> : null}
            {ends.forward ? <>
              <div aria-hidden="true" className="pointer-events-none absolute inset-y-px right-px z-20 w-10 rounded-r-lg bg-linear-to-l from-card to-transparent" />
              <EdgeArrow side="forward" label={t('timeline.next')} onClick={() => step(1)} />
            </> : null}
          </div>
        </div>
        {focused ? <MonthDetail month={focus} answer={focused} /> : null}
      </div>
    </TooltipProvider>
  );
}

const motion = (): ScrollBehavior => (window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth');

/** A month back or ahead, on the band's own edge: centred, or near the top when many lanes make the band tall. */
function EdgeArrow({ side, label, onClick }: { side: 'back' | 'forward'; label: string; onClick: () => void }) {
  const Icon = side === 'back' ? ChevronLeftIcon : ChevronRightIcon;
  return (
    <Button type="button" variant="outline" size="icon" aria-label={label} onClick={onClick}
      className={cn('absolute top-[min(50%,14rem)] z-30 -translate-y-1/2 rounded-full shadow-sm', side === 'back' ? cn(AFTER_NAMES, 'ml-2') : 'right-2')}>
      <Icon aria-hidden="true" />
    </Button>
  );
}

/** The lanes' names, held on the left while the months slide under them (icons only on a phone). */
function Names({ lanes }: { lanes: Line[] }) {
  return (
    <div aria-hidden="true" className={cn('sticky left-0 z-20 shrink-0 border-r bg-card', NAMES)}>
      <div className={HEAD} />
      <div className={STRIP} />
      {lanes.map((l) => (
        <div key={l.itemId} className={cn('flex items-center gap-2 px-3', LANE)}>
          <ProviderIcon name={l.provider?.name ?? l.displayName} iconUrl={l.provider?.iconUrl} color={l.provider?.labels[0]?.color} />
          <span className="hidden min-w-0 truncate text-sm md:block">{l.displayName}</span>
        </div>
      ))}
    </div>
  );
}

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

function MonthSegment({ month, answer, lanes, largest, today, focused, onFocus }: {
  month: string; answer: Answer; lanes: Line[]; largest: number; today: string; focused: boolean; onFocus: () => void;
}) {
  const { t } = useTranslation('subscriptions-board');
  const label = formatDate(month, { month: 'long', year: 'numeric' });
  const d = answer.data;
  const current = today.slice(0, 7);
  const past = month < current ? 1 : month === current ? dayOffset(today) : 0;

  return (
    <div role="group" aria-label={label} data-month={month} className={cn('w-[85vw] max-w-80 shrink-0 border-l md:w-80', focused && 'bg-accent/40')}>
      <div className={cn('space-y-2 px-3 pt-3', HEAD)}>
        <div className="flex items-baseline justify-between gap-2">
          <h2 className="text-sm font-semibold">
            <button type="button" aria-current={focused || undefined} onClick={onFocus}
              className="rounded-sm outline-none first-letter:uppercase hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50">{label}</button>
          </h2>
          {d ? <span className="truncate text-xs text-muted-foreground tabular-nums">
            {t('timeline.summary', { count: d.charges.length, total: d.total ? formatMoney(d.total) : '—' })}
          </span> : null}
        </div>
        {d?.total && largest > 0 ? (
          <div aria-hidden="true" className="h-1.5 rounded-full bg-muted">
            <div className="h-full rounded-full bg-chart-1" style={{ width: pct(toPlot(d.total.amount) / largest) }} />
          </div>
        ) : null}
      </div>
      <div className="relative min-h-32 pb-6">
        <div className="absolute inset-x-5 inset-y-0">
          {past > 0 ? <div aria-hidden="true" className="absolute top-0 bottom-6 -left-5 bg-muted/60" style={{ width: `calc(${pct(past)} + ${past === 1 ? '2.5rem' : '1.25rem'})` }} /> : null}
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
        </div>
        <div className={STRIP} />
        {lanes.map((l) => <Lane key={l.itemId} lines={d?.charges.filter((c) => c.itemId === l.itemId) ?? []} />)}
        {answer.error ? <div className="relative p-3"><ProblemMessage problem={present(toProblem(answer.error))} /></div>
          : !d ? (
            <Loading label={t('timeline.loading', { month: label })} className="absolute inset-x-3 top-6 bottom-8">
              <div className="flex h-full min-h-20 items-center justify-center rounded-md bg-muted text-xs text-muted-foreground">{t('timeline.loading', { month: label })}</div>
            </Loading>
          ) : null}
      </div>
    </div>
  );
}

/** One subscription's charges in a month, each on its day; two on the same day open as a list. */
function Lane({ lines }: { lines: Line[] }) {
  const { t } = useTranslation('subscriptions-board');
  return (
    <div className={cn('relative', LANE)}>
      <div className="absolute inset-x-5 inset-y-0">
        {[...byDay(lines)].map(([date, day]) => {
          const label = formatDate(date, { weekday: 'long', day: 'numeric', month: 'long' });
          return (
            <div key={date} className="absolute top-1/2 -translate-x-1/2 -translate-y-1/2" style={{ left: pct(dayOffset(date)) }}>
              {day.length === 1 ? <Disc c={day[0]!} /> : (
                <DayMore lines={day} label={label} className="size-7 rounded-full border bg-muted px-0" more={day.length}
                  moreLabel={t('timeline.several', { count: day.length, name: day[0]!.displayName, date: label })} />
              )}
            </div>
          );
        })}
      </div>
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

/** The month in focus, under the band: its server figures, then its charges day by day. */
function MonthDetail({ month, answer }: { month: string; answer: Answer }) {
  const { t } = useTranslation('subscriptions-board');
  const label = formatDate(month, { month: 'long', year: 'numeric' });
  const title = label.charAt(0).toUpperCase() + label.slice(1);
  const d = answer.data;
  if (answer.error) return <Section title={title}><ProblemMessage problem={present(toProblem(answer.error))} /></Section>;
  if (!d) return <Section title={title}><Loading label={t('timeline.loading', { month: label })}><Skeleton className="h-32 w-full" /></Loading></Section>;

  const horizon = d.findings.some((f) => f.code === 'subscriptions.horizon_reached');
  const beyond = horizon && d.charges.length === 0; // with charges, the forecast stops within the month
  return (
    <Section title={title}>
      <div className="space-y-4">
        <StatLine items={[
          { label: t('calendar.total'), value: d.total && !beyond ? formatMoney(d.total) : '—' },
          { label: t('calendar.count'), value: d.charges.length },
        ]} />
        {beyond ? <p className="text-sm text-muted-foreground">{t('calendar.beyond')}</p>
          : horizon ? <p className="text-sm text-muted-foreground">{t('calendar.truncated')}</p>
          : d.charges.length === 0 ? <p className="text-sm text-muted-foreground">{t('calendar.empty')}</p> : null}
        {d.charges.length > 0 ? <DayList lines={d.charges} label={t('calendar.by_day')} className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3" /> : null}
      </div>
    </Section>
  );
}
