import { createContext, useContext, useId, type ReactNode } from 'react';

/** The label text of the surrounding Field, for a control that names a part of itself after it (DateField's calendar button). */
export const FieldLabel = createContext('');
export const useFieldLabel = () => useContext(FieldLabel);

export type FieldControl = { id: string; 'aria-invalid'?: true; 'aria-describedby'?: string; 'aria-labelledby'?: string };

/** A label, its control, its hint and its error, tied together for assistive technology (aria-describedby, aria-invalid). */
/** `badge` sits beside the label, outside it: a form's box number (`7DB`) that the field's name does not carry. */
export function Field({ label, error, hint, badge, children, inline = false }: {
  label: string; error?: string; hint?: string; badge?: string; inline?: boolean; children: (control: FieldControl) => ReactNode;
}) {
  const id = useId();
  const errorId = useId();
  const hintId = useId();
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ');
  const control: FieldControl = { id };
  if (error) control['aria-invalid'] = true;
  if (describedBy) control['aria-describedby'] = describedBy;
  return (
    <FieldLabel value={label}>
      <div className={inline ? 'flex items-center gap-2' : 'space-y-1'}>
        {inline ? children(control) : null}
        {badge ? (
          <div className="flex items-center gap-1.5">
            <label htmlFor={id} className="block text-sm">{label}</label>
            <span className="rounded-md bg-muted px-1.5 text-xs text-muted-foreground">{badge}</span>
          </div>
        ) : <label htmlFor={id} className="block text-sm">{label}</label>}
        {inline ? null : children(control)}
        {hint ? <span id={hintId} className="block text-sm text-muted-foreground">{hint}</span> : null}
        {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
      </div>
    </FieldLabel>
  );
}
