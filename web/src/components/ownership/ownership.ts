import type { TFunction } from 'i18next';
import { z } from 'zod';
import '@/locales/forms';

export type OwnerRow = { personId: string; share: string };
/** #294: `shareLevel` only means something on a shared item; absent is `view`. */
export type OwnershipValues = { visibility: 'shared' | 'private'; owners: OwnerRow[]; shareLevel?: 'view' | 'edit' };

/**
 * A share typed in percent as basis points, by reading the text (`33.33` → 3333): no floating point touches it.
 * At most two decimals and at most 100 %; anything else is null.
 */
export function percentToBp(text: string): number | null {
  const match = /^(\d{1,3})(?:[.,](\d{1,2}))?$/.exec(text.trim());
  if (!match) return null;
  const bp = Number(match[1]) * 100 + Number((match[2] ?? '').padEnd(2, '0'));
  return bp <= 10000 ? bp : null;
}

/** Text of a share in basis points, for the field: `5000` → `50`, `3333` → `33.33` (digits only, no float). */
export function bpToPercent(bp: number): string {
  const s = String(bp).padStart(3, '0');
  return `${s.slice(0, -2)}.${s.slice(-2)}`.replace(/\.?0+$/, '');
}

/** Zod fields to spread into an object schema. */
export function ownershipShape(t: TFunction) {
  return {
    visibility: z.enum(['shared', 'private']),
    shareLevel: z.enum(['view', 'edit']).optional(),
    owners: z.array(z.object({ personId: z.string().min(1, t('edit.required')), share: z.string() })).min(1, t('edit.required')),
  };
}

/** The superRefine rules: shares sum to 100 %, a private item has one owner. */
export function checkOwnership(v: OwnershipValues, ctx: z.RefinementCtx, t: TFunction): void {
  const shares = v.owners.map((o) => percentToBp(o.share));
  if (shares.some((bp) => bp === null) || shares.reduce<number>((sum, bp) => sum + (bp ?? 0), 0) !== 10000)
    ctx.addIssue({ code: 'custom', path: ['owners'], message: t('field_error.core_share_sum') });
  if (v.visibility === 'private' && v.owners.length !== 1)
    ctx.addIssue({ code: 'custom', path: ['owners'], message: t('field_error.core_private_single_owner') });
}

/** `shareLevel` goes only at `edit`: the server reads an omitted level as `view`, and a private item has none. */
export function ownershipToWire(v: OwnershipValues): {
  visibility: 'shared' | 'private'; shareLevel?: 'edit'; owners: { personId: string; shareBp: number }[];
} {
  return {
    visibility: v.visibility, ...(v.visibility === 'shared' && v.shareLevel === 'edit' ? { shareLevel: 'edit' as const } : {}),
    owners: v.owners.map((o) => ({ personId: o.personId, shareBp: percentToBp(o.share) ?? 0 })),
  };
}

/** `owners[1].shareBp` → `owners.1.share`, `visibility` / `owners` as themselves; undefined otherwise. */
export function ownershipPath(path: string): 'visibility' | 'owners' | `owners.${number}.share` | undefined {
  if (path === 'visibility' || path === 'owners') return path;
  const share = /^owners\[(\d+)\]\.shareBp$/.exec(path);
  if (share) return `owners.${Number(share[1])}.share` as const;
  return undefined;
}
