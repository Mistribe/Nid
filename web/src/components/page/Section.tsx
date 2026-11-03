import { useId, type ReactNode, type RefObject } from 'react';

/** A titled region of a detail page, without a card: `headingRef` makes its heading a focus target. An action's outcome is a toast, not a line here. */
export function Section({ id: anchor, title, headingRef, guide, srTitle, children }: {
  /** The region's anchor, for a link to it (`#retraite`). */ id?: string; title: string; srTitle?: boolean; headingRef?: RefObject<HTMLHeadingElement | null>; guide?: string; children: ReactNode;
}) {
  const id = useId();
  return (
    <section id={anchor} aria-labelledby={id} data-guide={guide} className="space-y-2">
      <h2 id={id} ref={headingRef} tabIndex={headingRef ? -1 : undefined} className={srTitle ? 'sr-only' : 'text-lg font-semibold'}>{title}</h2>
      {children}
    </section>
  );
}
