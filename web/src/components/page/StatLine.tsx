import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

/**
 * Server figures inline, set apart by space rather than dividers (a divider left over at the start of a wrapped row
 * read as a stray mark); two per row on a phone. The tone is a colour on top of the sign the text carries.
 */
export function StatLine({ items }: { items: { label: string; value: ReactNode; tone?: 'up' | 'down' }[] }) {
  return (
    <dl className="grid grid-cols-2 gap-x-4 gap-y-3 md:flex md:flex-wrap md:gap-x-10">
      {items.map((item) => (
        <div key={item.label} className="min-w-0">
          <dt className="text-sm text-muted-foreground">{item.label}</dt>
          <dd className={cn('text-lg font-semibold tabular-nums', item.tone === 'up' && 'text-status-good-text', item.tone === 'down' && 'text-destructive')}>
            {item.value}
          </dd>
        </div>
      ))}
    </dl>
  );
}
