import type { ReactNode, Ref } from 'react';
import { usePageTrail, type Crumb } from './trail';

/** The top of a screen: title with its badges, actions (under the title on a phone), then a stats line. */
export function PageHeader({ crumbs, crumbLabel, title, titleRef, meta, actions, children }: {
  /** A title the screen moves focus to (after a decision) is focusable, never in the tab order. */
  titleRef?: Ref<HTMLHeadingElement>; crumbs?: Crumb[]; /** The label of the current page in the top bar's trail, when `title` is not plain text. */ crumbLabel?: string; title: ReactNode; meta?: ReactNode; actions?: ReactNode; children?: ReactNode;
}) {
  usePageTrail({ crumbs, current: crumbLabel ?? (typeof title === 'string' ? title : undefined) });
  return (
    <header data-page-header className="space-y-3">
      {/* From sm up the actions keep the title line (design-system.md §6.1): the title and its badges wrap first. */}
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 sm:flex-nowrap sm:items-start">
        <div className="flex min-w-0 flex-wrap items-center gap-2 sm:flex-1">
          <h1 ref={titleRef} tabIndex={titleRef ? -1 : undefined} className="text-large-title break-words outline-none">{title}</h1>
          {meta}
        </div>
        {actions ? <div data-page-actions className="flex flex-wrap items-center gap-2 sm:shrink-0">{actions}</div> : null}
      </div>
      {children}
    </header>
  );
}
