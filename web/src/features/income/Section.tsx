import { useId, type ReactNode, type RefObject } from 'react';

/** A titled region of the source's page: `headingRef` makes its heading a focus target, `announcement` its live region. */
export function Section({ title, headingRef, announcement, guide, children }: {
  title: string; headingRef?: RefObject<HTMLHeadingElement | null>; announcement?: string; guide?: string; children: ReactNode;
}) {
  const id = useId();
  return (
    <section aria-labelledby={id} data-guide={guide} className="space-y-2">
      <h2 id={id} ref={headingRef} tabIndex={headingRef ? -1 : undefined} className="text-lg font-semibold">{title}</h2>
      {announcement !== undefined ? <p role="status" aria-live="polite">{announcement}</p> : null}
      {children}
    </section>
  );
}
