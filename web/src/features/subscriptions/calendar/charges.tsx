import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import type { Charges } from '@/api/subscriptions';
import { Money } from '@/components/Money';
import { ProviderIcon } from '@/components/ProviderIcon';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn } from '@/lib/utils';

/** The calendar's and the timeline's pieces (issue #65). */
export type Line = Charges['charges'][number];

/** A calendar date as French text, read in UTC so the day never shifts. */
export const formatDate = (iso: string, options: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat('fr-FR', { timeZone: 'UTC', ...options })
    .format(Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10) || '1')));

/** A charge, linked to its subscription: the converted amount when the server has one, its own price otherwise. */
export function ChargeLink({ c, compact = false }: { c: Line; compact?: boolean }) {
  return (
    <Link to="/subscriptions/$id" params={{ id: c.itemId }}
      className={cn('flex min-w-0 items-center gap-2 rounded-sm px-1 hover:bg-accent', compact ? 'text-xs' : 'py-1 text-sm')}>
      <ProviderIcon name={c.provider?.name ?? c.displayName} iconUrl={c.provider?.iconUrl} color={c.provider?.labels[0]?.color} />
      <span className="min-w-0 flex-1 truncate font-medium">{c.displayName}</span>
      <span className="whitespace-nowrap"><Money value={c.converted ?? c.price} tabular /></span>
    </Link>
  );
}

/** « +n »: the day's whole list in a popover; `label` names the day. */
export function DayMore({ lines, label, more, moreLabel, className }: {
  lines: Line[]; label: string; more: ReactNode; moreLabel: string; className?: string;
}) {
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" variant="ghost" size="sm" className={cn('h-6 px-1 text-xs', className)} aria-label={moreLabel}>
          {more}
        </Button>
      </PopoverTrigger>
      <PopoverContent aria-label={label} className="max-h-[min(20rem,var(--radix-popover-content-available-height))] w-80 space-y-1 overflow-y-auto p-2">
        <p aria-hidden="true" className="px-1 text-sm font-medium first-letter:uppercase">{label}</p>
        {lines.map((c, i) => <ChargeLink key={`${c.itemId}/${i}`} c={c} />)}
      </PopoverContent>
    </Popover>
  );
}

/** The charges grouped by day, in the server's order. */
export function byDay(lines: Line[]): Map<string, Line[]> {
  const days = new Map<string, Line[]>();
  for (const c of lines) days.set(c.date, [...(days.get(c.date) ?? []), c]);
  return days;
}

/** Charges day by day: each day's heading, its charges under it. */
export function DayList({ lines, label, className }: { lines: Line[]; label: string; className?: string }) {
  return (
    <ul aria-label={label} className={className}>
      {[...byDay(lines)].map(([date, day]) => (
        <li key={date} className="space-y-1">
          <h3 className="text-sm font-semibold">{formatDate(date, { weekday: 'short', day: 'numeric', month: 'short' })}</h3>
          {day.map((c, i) => <ChargeLink key={`${c.itemId}/${i}`} c={c} />)}
        </li>
      ))}
    </ul>
  );
}
