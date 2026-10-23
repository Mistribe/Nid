import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

/**
 * Server figures in columns of equal width that fill the line (at least 10 rem each): five fit in one row at 1 280 px, and a
 * row that wraps keeps its figures in the same columns rather than leaving one alone (no divider either: one left over at the
 * start of a wrapped row read as a stray mark); two per row on a phone. The tone is a colour on top of the sign the text carries.
 */
export function StatLine({ items }: { items: { label: string; value: ReactNode; tone?: 'up' | 'down' }[] }) {
  return (
    <dl className="grid grid-cols-2 gap-x-6 gap-y-3 md:grid-cols-[repeat(auto-fill,minmax(10rem,1fr))]">
      {items.map((item) => (
        <div key={item.label} className="min-w-0">
          <dt className="text-sm text-muted-foreground">{item.label}</dt>
          <dd className={cn('text-xl font-semibold tracking-tight', item.tone === 'up' && 'text-status-good-text', item.tone === 'down' && 'text-destructive')}>
            {item.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}
