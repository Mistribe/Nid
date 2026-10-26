import { EllipsisIcon } from 'lucide-react';
import type { Ref } from 'react';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';

type Item = { label: string; onSelect: () => void; destructive?: boolean };
/** A plain link that downloads (a file route of the API, no client-side route behind it). */
type Download = { label: string; href: string };
/** An entry that opens a submenu instead of acting by itself. */
type Group = { label: string; items: Item[] };

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
          <DropdownMenuItem key={item.label} variant={item.destructive ? 'destructive' : 'default'} onSelect={item.onSelect}>{item.label}</DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
