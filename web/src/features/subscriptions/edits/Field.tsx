import { useId, type ReactNode } from 'react';

export type FieldControl = { id: string; 'aria-invalid'?: true; 'aria-describedby'?: string };

/** A label, its control, its hint and its error, tied together for assistive technology (aria-describedby, aria-invalid). */
export function Field({ label, error, hint, children, inline = false }: {
  label: string; error?: string; hint?: string; inline?: boolean; children: (control: FieldControl) => ReactNode;
}) {
  const id = useId();
  const errorId = useId();
  const hintId = useId();
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ');
  const control: FieldControl = { id };
  if (error) control['aria-invalid'] = true;
  if (describedBy) control['aria-describedby'] = describedBy;
  return (
    <div className={inline ? 'flex items-center gap-2' : 'space-y-1'}>
      {inline ? children(control) : null}
      <label htmlFor={id} className="block text-sm">{label}</label>
      {inline ? null : children(control)}
      {hint ? <span id={hintId} className="block text-sm text-muted-foreground">{hint}</span> : null}
      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
    </div>
  );
}
