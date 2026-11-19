import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select';
import type { FieldControl } from './Field';
import { NONE, runs, type Option } from './options';

export type { Option } from './options';

export type SelectFieldProps = Partial<FieldControl> & {
  value: string; onValueChange: (v: string) => void; options: readonly Option[]; placeholder?: string; disabled?: boolean;
};

export function SelectField({ value, onValueChange, options, placeholder, disabled, ...control }: SelectFieldProps) {
  const hasNone = options.some((o) => o.value === '');
  return (
    <Select
      value={value === '' && hasNone ? NONE : value}
      onValueChange={(v) => onValueChange(v === NONE ? '' : v)}
      disabled={disabled}
    >
      <SelectTrigger className="w-full" {...control}>
        <SelectValue placeholder={placeholder} />
      </SelectTrigger>
      <SelectContent>
        {runs(options).map((run, i) => {
          const items = run.options.map((o) => <SelectItem key={o.value} value={o.value === '' ? NONE : o.value}>{o.label}</SelectItem>);
          return run.group ? <SelectGroup key={`group-${i}`}><SelectLabel>{run.group}</SelectLabel>{items}</SelectGroup> : items;
        })}
      </SelectContent>
    </Select>
  );
}
