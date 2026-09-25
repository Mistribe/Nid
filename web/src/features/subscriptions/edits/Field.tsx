import { useId, type ReactNode } from 'react';

export type FieldControl = { id: string; 'aria-invalid'?: true; 'aria-describedby'?: string };

/** A label, its control and its error, tied together for assistive technology (aria-describedby, aria-invalid). */
export function Field({ label, error, children, inline = false }: {
  label: string; error?: string; inline?: boolean; children: (control: FieldControl) => ReactNode;
}) {
  const id = useId();
  const errorId = useId();
  const control: FieldControl = error ? { id, 'aria-invalid': true, 'aria-describedby': errorId } : { id };
  return (
    <div className={inline ? 'flex items-center gap-2' : 'space-y-1'}>
      {inline ? children(control) : null}
      <label htmlFor={id} className="block text-sm">{label}</label>
      {inline ? null : children(control)}
      {error ? <span id={errorId} className="block text-sm text-destructive">{error}</span> : null}
    </div>
  );
}
