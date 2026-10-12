import { useId, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';

/**
 * A folded part of the form (precision 13): a button with `aria-expanded`, its fields mounted only while open (RHF keeps
 * their values); it opens itself while a field inside holds an error, so a refusal is never hidden.
 */
export function Folded({ label, hasError, children }: { label: string; hasError: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const shown = open || hasError;
  return (
    <div className="space-y-2">
      <Button type="button" variant="ghost" size="sm" aria-expanded={shown} aria-controls={shown ? id : undefined} onClick={() => setOpen(!shown)}>
        {label}
      </Button>
      {shown ? <div id={id} className="space-y-2">{children}</div> : null}
    </div>
  );
}
