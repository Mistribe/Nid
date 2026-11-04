import type { LucideIcon } from 'lucide-react';
import { cn } from '@/lib/utils';

/** A module's colour slot: `primary` is Nid itself (the theme accent), `neutral` the account pages, 1–8 a module (dataviz.md §5). */
export type Tile = 'primary' | 'neutral' | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8;

/** A module's glyph, white on its colour: a marker beside the label (the label stays the name). */
export function ModuleTile({ icon: Icon, tile, size = 'sm' }: { icon: LucideIcon; tile: Tile; size?: 'sm' | 'lg' }) {
  return (
    <span
      data-tile={String(tile)}
      aria-hidden="true"
      // A dark theme's accent is light: its glyph takes `--primary-foreground` (4.5:1, contrast.test.ts) instead of white.
      style={tile === 'primary' ? { background: 'var(--primary)', color: 'var(--primary-foreground)' } : { background: `var(--tile-${tile})` }}
      // The top light fades out by 18 %, above the glyph (from 19 %): the glyph sits on the bare colour (contrast.test.ts reads both stops).
      className={cn(
        'relative inline-grid shrink-0 place-items-center rounded-[28%] text-white before:absolute before:inset-0 before:rounded-[inherit] before:bg-linear-to-b before:from-white/20 before:to-transparent before:to-18%',
        size === 'sm' ? 'size-[22px]' : 'size-12',
      )}
    >
      {/* `text-current` and `!` beat the command menu's `[cmdk-item] svg` rules (grey, size-5). */}
      <Icon strokeWidth={2.2} className="relative size-[62%]! text-current" />
    </span>
  );
}
