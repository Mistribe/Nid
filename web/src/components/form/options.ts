import type { ReactNode } from 'react';

/** `icon` is decorative: the label always names the option. */
export type Option = { value: string; label: string; icon?: ReactNode };

/** Radix Select refuses an empty item value: the "none" option travels as this sentinel. */
export const NONE = '__none__';
