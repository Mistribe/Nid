import { RadioGroup } from 'radix-ui';
import { useLayoutEffect, useRef, useState } from 'react';
import type { Option } from './options';

/** A pill slides under the checked option: measured after each change and on resize, still on the first paint. */
function usePill(value: string) {
  const root = useRef<HTMLDivElement>(null);
  const [pill, setPill] = useState<{ left: number; width: number; moved: boolean } | null>(null);
  useLayoutEffect(() => {
    const el = root.current;
    if (!el) return;
    const measure = () => {
      const checked = el.querySelector<HTMLElement>('[data-state=checked]');
      setPill((prev) => (checked ? { left: checked.offsetLeft, width: checked.offsetWidth, moved: prev !== null } : null));
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [value]);
  return [root, pill] as const;
}

export function Segmented({ value, onValueChange, options, ...aria }: {
  value: string; onValueChange: (v: string) => void; options: readonly Option[];
  'aria-labelledby'?: string; 'aria-label'?: string; 'aria-describedby'?: string; 'aria-invalid'?: true;
}) {
  const [root, pill] = usePill(value);
  return (
    <RadioGroup.Root ref={root} value={value} onValueChange={onValueChange} {...aria}
      className="relative inline-flex rounded-md border border-input bg-muted p-0.5">
      {pill ? (
        <span aria-hidden="true" style={{ left: pill.left, width: pill.width }}
          className={`absolute inset-y-0.5 rounded bg-background shadow-xs ${pill.moved ? 'transition-[left,width] duration-300 ease-emphasized' : ''}`} />
      ) : null}
      {options.map((o) => (
        <RadioGroup.Item key={o.value} value={o.value}
          className="relative rounded px-3 py-1 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[state=checked]:font-medium">
          {o.label}
        </RadioGroup.Item>
      ))}
    </RadioGroup.Root>
  );
}
