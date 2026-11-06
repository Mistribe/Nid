import { Link, type LinkProps } from '@tanstack/react-router';
import { ChevronRight, type LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { ModuleTile, type Tile } from '@/components/ModuleTile';

/** Rows in one group: the caller's `SectionCard` is the group, so the list draws no card of its own. */
export function GroupedList({ children, labelledBy }: { children: ReactNode; labelledBy?: string }) {
  // The inset line sits on every row but the first; a row with a tile starts it after the tile (see GroupedRow).
  return (
    <ul aria-labelledby={labelledBy}
      className="text-sm [&>li+li]:before:absolute [&>li+li]:before:top-0 [&>li+li]:before:right-0 [&>li+li]:before:border-t [&>li+li]:before:border-separator">
      {children}
    </ul>
  );
}

/** One link over the whole row: tile, title over detail, an amount, a chevron. */
export function GroupedRow({ link, tile, title, detail, trailing }: {
  link: LinkProps; tile?: { icon: LucideIcon; tile: Tile }; title: ReactNode; detail?: ReactNode; trailing?: ReactNode;
}) {
  return (
    // 34 px = the `ModuleTile` sm (22 px) + the `gap-3` (12 px) of the link below.
    <li data-tile={tile ? String(tile.tile) : undefined} className="relative before:left-0 data-tile:before:left-[34px]">
      <Link {...link} className="flex min-h-11 items-center gap-3 rounded-md py-2 outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">
        {tile ? <ModuleTile icon={tile.icon} tile={tile.tile} /> : null}
        {/* Divs, not spans: a detail may hold a block (a goal's progress bar), which a span cannot.
            A grid so the amount sits on the title's first line while the title wraps to two (issue 390), DOM order unchanged. */}
        <div className="grid min-w-0 flex-1 grid-cols-[minmax(0,1fr)_auto] items-start">
          <div className="line-clamp-2 font-medium">{title}</div>
          {/* A space between the parts: the accessible name reads « title detail amount », not run together. */}
          {detail ? <>{' '}<div className="col-start-1 text-xs text-muted-foreground">{detail}</div></> : null}
          {trailing ? <>{' '}<span className="col-start-2 row-start-1 pl-3 font-medium tabular-nums">{trailing}</span></> : null}
        </div>
        <ChevronRight aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
      </Link>
    </li>
  );
}
