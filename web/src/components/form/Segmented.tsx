import { RadioGroup } from 'radix-ui';
import { useLayoutEffect, useRef, useState } from 'react';
import type { Option } from './options';

/**
 * A pill slides under the checked option: measured after each change and on resize, still on the first paint. Its top
 * and height are measured too: in a narrow place the options wrap onto a second row rather than break their labels.
 */
function usePill(value: string) {
  const root = useRef<HTMLDivElement>(null);
  const [pill, setPill] = useState<{ left: number; top: number; width: number; height: number; moved: boolean } | null>(null);
  useLayoutEffect(() => {
    const el = root.current;
    if (!el) return;
    const measure = () => {
      const checked = el.querySelector<HTMLElement>('[data-state=checked]');
      setPill((prev) => (checked ? { left: checked.offsetLeft, top: checked.offsetTop, width: checked.offsetWidth, height: checked.offsetHeight, moved: prev !== null } : null));
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
      className="relative inline-flex max-w-full flex-wrap rounded-[1.125rem] border border-input bg-muted p-0.5">
      {pill ? (
        <span aria-hidden="true" style={{ left: pill.left, top: pill.top, width: pill.width, height: pill.height }}
          className={`absolute rounded-full bg-background shadow-xs ${pill.moved ? 'transition-[left,top,width,height] duration-300 ease-emphasized' : ''}`} />
      ) : null}
      {options.map((o) => (
        <RadioGroup.Item key={o.value} value={o.value}
          className="relative rounded-full px-3 py-1 text-sm whitespace-nowrap outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[state=checked]:font-medium">
          {o.label}
        </RadioGroup.Item>
      ))}
    </RadioGroup.Root>
  );
}
