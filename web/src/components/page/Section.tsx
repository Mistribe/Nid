import { useId, type ReactNode, type RefObject } from 'react';

/**
 * A titled region of a detail page, without a card: `headingRef` makes its heading a focus target. Its `actions` sit at
 * the end of the title line, never under it. An action's outcome is a toast, not a line here.
 */
export function Section({ id: anchor, title, headingRef, guide, srTitle, actions, children }: {
  /** The region's anchor, for a link to it (`#retraite`). */ id?: string; title: string; srTitle?: boolean; headingRef?: RefObject<HTMLHeadingElement | null>; guide?: string; actions?: ReactNode; children: ReactNode;
}) {
  const id = useId();
  const heading = <h2 id={id} ref={headingRef} tabIndex={headingRef ? -1 : undefined} className={srTitle ? 'sr-only' : 'text-title'}>{title}</h2>;
  return (
    <section id={anchor} aria-labelledby={id} data-guide={guide} className="space-y-2">
      {actions ? <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">{heading}<div data-section-actions className="flex flex-wrap items-center gap-2">{actions}</div></div> : heading}
      {children}
    </section>
  );
}
