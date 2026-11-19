import type { ReactNode } from 'react';

/**
 * `icon` is decorative: the label always names the option; a `disabled` one is listed but cannot be picked (its label says why).
 * `group` heads the run of consecutive options that share it.
 */
export type Option = { value: string; label: string; icon?: ReactNode; disabled?: boolean; group?: string };

/** Consecutive options under their shared `group` heading (none: a run without one). */
export function runs(options: readonly Option[]): { group?: string; options: Option[] }[] {
  const out: { group?: string; options: Option[] }[] = [];
  for (const o of options) {
    const last = out.at(-1);
    if (last && last.group === o.group) last.options.push(o);
    else out.push({ group: o.group, options: [o] });
  }
  return out;
}

/** Radix Select refuses an empty item value: the "none" option travels as this sentinel. */
export const NONE = '__none__';
