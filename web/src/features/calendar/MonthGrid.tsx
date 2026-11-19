import { Fragment, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn } from '@/lib/utils';
import { byDay, formatDate, monthGrid } from './calendarDates';

// 2024-01-01 was a Monday.
const WEEKDAYS = Array.from({ length: 7 }, (_, i) => formatDate(`2024-01-0${i + 1}`, { weekday: 'short' }));
const SHOWN = 3;

export type MonthGridLabels = {
  /** The grid's accessible name. */
  grid: string;
  /** The phone's list of days. */
  byDay: string;
  /** « +n » and its accessible name, for a day with more items than its cell shows. */
  more: (count: number) => string;
  moreLabel: (count: number) => string;
};

/**
 * Issue #239: a month of dated items, generic over the item: a grid of weeks (desktop) or a list of days (phone), today marked,
 * three items a cell at most, the rest behind « +n ». `render` draws an item, `compact` in a cell. Issue #241: `aside` draws what a
 * day sums up (its totals) beside its number, and beside its heading on a phone.
 */
export function MonthGrid<T>({ month, today, items, dateOf, render, labels, aside }: {
  month: string; today: string; items: readonly T[]; dateOf: (item: T) => string;
  render: (item: T, compact: boolean) => ReactNode; labels: MonthGridLabels; aside?: (date: string) => ReactNode;
}) {
  const days = byDay(items, dateOf);
  const all = (list: T[]) => list.map((item, i) => <Fragment key={i}>{render(item, false)}</Fragment>);
  return (
    <>
      <div role="group" aria-label={labels.grid} className="hidden overflow-hidden rounded-lg border md:grid md:grid-cols-7">
        {WEEKDAYS.map((w) => <div key={w} aria-hidden="true" className="px-2 py-1 text-xs text-muted-foreground">{w}</div>)}
        {monthGrid(month).map((cell) => {
          const list = days.get(cell.date) ?? [];
          const shown = list.length > SHOWN ? list.slice(0, SHOWN - 1) : list;
          const hidden = list.length - shown.length;
          const label = formatDate(cell.date, { weekday: 'long', day: 'numeric', month: 'long' });
          return (
            <div key={cell.date} role="group" aria-label={label} aria-current={cell.date === today ? 'date' : undefined}
              className={cn('min-h-24 min-w-0 space-y-1 border-t border-l p-1 [&:nth-child(7n+1)]:border-l-0', !cell.inMonth && 'bg-muted/40 text-muted-foreground')}>
              <div className="flex items-start justify-between gap-1">
                <div aria-hidden="true" className={cn('inline-flex size-6 shrink-0 items-center justify-center rounded-full text-xs',
                  cell.date === today && 'bg-primary font-semibold text-primary-foreground')}>{Number(cell.date.slice(8))}</div>
                {aside?.(cell.date)}
              </div>
              {shown.map((item, i) => <Fragment key={i}>{render(item, true)}</Fragment>)}
              {hidden > 0 ? (
                <Popover>
                  <PopoverTrigger asChild>
                    <Button type="button" variant="ghost" size="sm" className="h-6 px-1 text-xs" aria-label={labels.moreLabel(hidden)}>{labels.more(hidden)}</Button>
                  </PopoverTrigger>
                  <PopoverContent aria-label={label} className="max-h-[min(20rem,var(--radix-popover-content-available-height))] w-80 space-y-1 overflow-y-auto p-2">
                    <p aria-hidden="true" className="px-1 text-sm font-medium first-letter:uppercase">{label}</p>
                    {all(list)}
                  </PopoverContent>
                </Popover>
              ) : null}
            </div>
          );
        })}
      </div>

      {days.size > 0 ? (
        <ul aria-label={labels.byDay} className="space-y-4 md:hidden">
          {[...days].map(([date, list]) => (
            <li key={date} className="space-y-1">
              <div className="flex items-start justify-between gap-2">
                <h3 className={cn('text-sm font-semibold first-letter:uppercase', date === today && 'text-primary')}>
                  {formatDate(date, { weekday: 'long', day: 'numeric', month: 'long' })}
                </h3>
                {aside?.(date)}
              </div>
              {all(list)}
            </li>
          ))}
        </ul>
      ) : null}
    </>
  );
}
