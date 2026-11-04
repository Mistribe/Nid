import { EllipsisIcon } from 'lucide-react';
import type { Ref } from 'react';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';

/** `phone`: a header's secondary folded into `⋯` below `sm`, where the button itself is `max-sm:hidden`. */
type Item = { label: string; onSelect: () => void; destructive?: boolean; disabled?: boolean; phone?: boolean };
/** A plain link that downloads (a file route of the API, no client-side route behind it). */
type Download = { label: string; href: string };
/** An entry that opens a submenu instead of acting by itself. */
type Group = { label: string; items: Item[] };

/** The header button the focus goes back to, or the `⋯` trigger while that button is folded away (`max-sm:hidden`). */
export const shownOr = (button: HTMLElement | null, menu: HTMLElement | null) => (button?.checkVisibility?.() === false ? menu : button);

/** A row's secondary actions behind `⋯`; nothing at all when the row has none. */
export function RowMenu({ label, items, triggerRef, id }: {
  label: string; id?: string; items: (Item | Download | Group)[]; triggerRef?: Ref<HTMLButtonElement>;
}) {
  if (items.length === 0) return null;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button ref={triggerRef} id={id} variant="ghost" size="icon" aria-label={label}><EllipsisIcon aria-hidden="true" /></Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        {items.map((item) => 'items' in item ? (
          <DropdownMenuSub key={item.label}>
            <DropdownMenuSubTrigger>{item.label}</DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              {item.items.map((sub) => <DropdownMenuItem key={sub.label} onSelect={sub.onSelect}>{sub.label}</DropdownMenuItem>)}
            </DropdownMenuSubContent>
          </DropdownMenuSub>
        ) : 'href' in item ? (
          <DropdownMenuItem key={item.label} asChild><a href={item.href} download>{item.label}</a></DropdownMenuItem>
        ) : (
          <DropdownMenuItem key={item.label} className={item.phone ? 'sm:hidden' : undefined} variant={item.destructive ? 'destructive' : 'default'} disabled={item.disabled} onSelect={item.onSelect}>{item.label}</DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
