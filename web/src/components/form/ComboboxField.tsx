import { lazy, Suspense, useState } from 'react';
import { ChevronsUpDownIcon } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import type { SelectFieldProps } from './SelectField';

const ComboboxList = lazy(() => import('./ComboboxList'));

export function ComboboxField({ value, onValueChange, options, placeholder, disabled, searchPlaceholder, empty, ...control }:
  SelectFieldProps & { searchPlaceholder: string; empty: string }) {
  const [open, setOpen] = useState(false);
  const selected = options.find((o) => o.value === value);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button type="button" variant="outline" role="combobox" aria-expanded={open} disabled={disabled}
          className="w-full justify-between font-normal" {...control}>
          <span className={selected ? 'flex items-center gap-2' : 'text-muted-foreground'}>{selected?.icon}{selected?.label ?? placeholder}</span>
          <ChevronsUpDownIcon className="opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-(--radix-popover-trigger-width) p-0" align="start">
        <Suspense fallback={null}>
          <ComboboxList options={options} searchPlaceholder={searchPlaceholder} empty={empty}
            onPick={(v) => { onValueChange(v); setOpen(false); }} />
        </Suspense>
      </PopoverContent>
    </Popover>
  );
}
