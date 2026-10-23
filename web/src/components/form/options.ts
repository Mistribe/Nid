import type { ReactNode } from 'react';

/** `icon` is decorative: the label always names the option; a `disabled` one is listed but cannot be picked (its label says why). */
export type Option = { value: string; label: string; icon?: ReactNode; disabled?: boolean };

/** Radix Select refuses an empty item value: the "none" option travels as this sentinel. */
export const NONE = '__none__';
