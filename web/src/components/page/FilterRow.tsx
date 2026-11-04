import { FieldLabel } from '@/components/form/Field';
import { cn } from '@/lib/utils';
import { useId, type ReactNode } from 'react';

/** The filters of a page or a tab: one wrapping line of controls, each labelled before it (spec §4.2). */
export function FilterRow({ label, children }: { label: string; children: ReactNode }) {
  return <div role="group" aria-label={label} className="flex flex-wrap items-center gap-x-4 gap-y-2">{children}</div>;
}

/**
 * A visible label before its control; the control names itself with `aria-labelledby={labelId}` (a `DateField` names its
 * calendar button after it). A text input or a date field takes `id={controlId}` and its caller passes `labelFor`, so clicking
 * the label focuses it; a select or a segmented control gets no `for`: a Radix select would open on that click.
 */
export function FilterField({ label, labelFor = false, className, children }: {
  label: string; labelFor?: boolean; className?: string; children: (labelId: string, controlId: string) => ReactNode;
}) {
  const labelId = useId();
  const controlId = useId();
  return (
    <div data-filter-field className={cn('inline-flex items-center gap-2', className)}>
      <label id={labelId} htmlFor={labelFor ? controlId : undefined} className="shrink-0 text-sm whitespace-nowrap text-muted-foreground">{label}</label>
      <FieldLabel value={label}>{children(labelId, controlId)}</FieldLabel>
    </div>
  );
}
