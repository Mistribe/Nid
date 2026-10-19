import { RadioGroup } from 'radix-ui';
import type { Option } from './options';

export function Segmented({ value, onValueChange, options, ...aria }: {
  value: string; onValueChange: (v: string) => void; options: readonly Option[];
  'aria-labelledby'?: string; 'aria-label'?: string; 'aria-describedby'?: string; 'aria-invalid'?: true;
}) {
  return (
    <RadioGroup.Root value={value} onValueChange={onValueChange} {...aria}
      className="inline-flex rounded-md border border-input bg-muted p-0.5">
      {options.map((o) => (
        <RadioGroup.Item key={o.value} value={o.value}
          className="rounded px-3 py-1 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[state=checked]:bg-background data-[state=checked]:font-medium data-[state=checked]:shadow-xs">
          {o.label}
        </RadioGroup.Item>
      ))}
    </RadioGroup.Root>
  );
}
