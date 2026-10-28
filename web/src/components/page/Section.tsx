import { useId, type ReactNode, type RefObject } from 'react';

/** A titled region of a detail page, without a card: `headingRef` makes its heading a focus target, `announcement` its live region. */
export function Section({ id: anchor, title, headingRef, announcement, guide, srTitle, children }: {
  /** The region's anchor, for a link to it (`#retraite`). */ id?: string; title: string; srTitle?: boolean; headingRef?: RefObject<HTMLHeadingElement | null>; announcement?: string; guide?: string; children: ReactNode;
}) {
  const id = useId();
  return (
    <section id={anchor} aria-labelledby={id} data-guide={guide} className="space-y-2">
      <h2 id={id} ref={headingRef} tabIndex={headingRef ? -1 : undefined} className={srTitle ? 'sr-only' : 'text-lg font-semibold'}>{title}</h2>
      {announcement !== undefined ? <p role="status" aria-live="polite">{announcement}</p> : null}
      {children}
    </section>
  );
}
