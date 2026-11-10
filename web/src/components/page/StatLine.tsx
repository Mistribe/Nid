import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

/**
 * Server figures in columns of equal width that fill the line (at least 10 rem each): five fit in one row at 1 280 px, and a
 * row that wraps keeps its figures in the same columns rather than leaving one alone (no divider either: one left over at the
 * start of a wrapped row read as a stray mark); two per row on a phone. What qualifies a figure (a unit, a date, a count) goes
 * in its `hint`, the small line under it, as in the Hero: the figure itself stays short enough for one line.
 */
export function StatLine({ items, className }: { items: { label: string; value: ReactNode; hint?: ReactNode }[]; className?: string }) {
  return (
    <dl className={cn('grid grid-cols-2 gap-x-6 gap-y-3 md:grid-cols-[repeat(auto-fill,minmax(10rem,1fr))]', className)}>
      {items.map((item) => (
        <div key={item.label} className="min-w-0">
          <dt className="text-label text-muted-foreground">{item.label}</dt>
          <dd className="text-figure break-words">{item.value}</dd>
          {item.hint ? <dd className="text-sm text-muted-foreground">{item.hint}</dd> : null}
        </div>
      ))}
    </dl>
  );
}
