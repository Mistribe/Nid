import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command';
import type { Option } from './options';

/** Loaded lazily by ComboboxField so cmdk stays out of the initial bundle. */
export default function ComboboxList({ options, onPick, searchPlaceholder, empty }: {
  options: readonly Option[]; onPick: (v: string) => void; searchPlaceholder: string; empty: string;
}) {
  return (
    <Command label={searchPlaceholder}>
      <CommandInput placeholder={searchPlaceholder} />
      <CommandList>
        <CommandEmpty>{empty}</CommandEmpty>
        {options.map((o) => (
          <CommandItem key={o.value} value={o.label} onSelect={() => onPick(o.value)}>{o.icon}{o.label}</CommandItem>
        ))}
      </CommandList>
    </Command>
  );
}
