import type { ComponentProps, ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { PageHeader } from './PageHeader';

/**
 * A creation or edit page (design-system.md §6.1): the header where every page has it, the form below in a column of
 * reading width from the left edge, its actions at its foot (`FormActions`).
 */
export function FormPage({ crumbs, title, width = 'max-w-3xl', children }: {
  crumbs: ComponentProps<typeof PageHeader>['crumbs']; title: ReactNode; width?: 'max-w-2xl' | 'max-w-3xl'; children: ReactNode;
}) {
  return (
    <article className={cn('w-full space-y-6', width)}>
      <PageHeader crumbs={crumbs} title={title} />
      {children}
    </article>
  );
}

/** A form's buttons, set apart at its foot: the primary action last, at the end of the line. */
export function FormActions({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cn('mt-2 flex flex-wrap items-center justify-end gap-2 border-t pt-4', className)}>{children}</div>;
}
