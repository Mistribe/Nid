import { EllipsisIcon } from 'lucide-react';
import type { Ref } from 'react';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';

/** A row's secondary actions behind `⋯`; nothing at all when the row has none. */
export function RowMenu({ label, items, triggerRef, id }: {
  label: string; id?: string; items: { label: string; onSelect: () => void; destructive?: boolean }[]; triggerRef?: Ref<HTMLButtonElement>;
}) {
  if (items.length === 0) return null;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button ref={triggerRef} id={id} variant="ghost" size="icon" aria-label={label}><EllipsisIcon aria-hidden="true" /></Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        {items.map((item) => (
          <DropdownMenuItem key={item.label} variant={item.destructive ? 'destructive' : 'default'} onSelect={item.onSelect}>{item.label}</DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
