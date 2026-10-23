import type { ComponentProps, ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { PageHeader } from './PageHeader';

/**
 * A creation or edit page (design-system.md §6.1): a centred column of reading width, the header above, the form on a
 * card below it, its actions at the foot of the card (`FormActions`).
 */
export function FormPage({ crumbs, title, width = 'max-w-3xl', children }: {
  crumbs: ComponentProps<typeof PageHeader>['crumbs']; title: ReactNode; width?: 'max-w-2xl' | 'max-w-3xl'; children: ReactNode;
}) {
  return (
    <article className={cn('mx-auto w-full space-y-4', width)}>
      <PageHeader crumbs={crumbs} title={title} />
      <div className="rounded-xl border bg-card p-4 shadow-card sm:p-6">{children}</div>
    </article>
  );
}

/** A form's buttons, set apart at its foot: the primary action last, at the end of the line. */
export function FormActions({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('mt-2 flex flex-wrap items-center justify-end gap-2 border-t pt-4', className)}>{children}</div>;
}
