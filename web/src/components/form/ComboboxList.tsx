import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import type { Option } from './options';

/** Loaded lazily by ComboboxField so cmdk stays out of the initial bundle. */
export default function ComboboxList({ options, onPick, searchPlaceholder, empty, onSearch, more }: {
  options: readonly Option[]; onPick: (v: string) => void; searchPlaceholder: string; empty: string;
  onSearch?: (q: string) => void; more?: string;
}) {
  return (
    <Command shouldFilter={!onSearch} label={searchPlaceholder}>
      <CommandInput onValueChange={onSearch} placeholder={searchPlaceholder} />
      <CommandList>
        <CommandEmpty>{empty}</CommandEmpty>
        {options.map((o) => (
          <CommandItem key={o.value} value={onSearch ? o.value : o.label} disabled={o.disabled} onSelect={() => onPick(o.value)}>{o.icon}{o.label}</CommandItem>
        ))}
        {more ? <p className="px-3 py-2 text-xs text-muted-foreground">{more}</p> : null}
      </CommandList>
    </Command>
  );
}
