import type { TFunction } from 'i18next';

export const KINDS = ['checking', 'savings', 'pea', 'life_insurance', 'per', 'brokerage', 'other'] as const;
export const CADENCES = ['monthly', 'quarterly', 'yearly', 'manual'] as const;
export const FLOW_KINDS = ['contribution', 'withdrawal', 'expense', 'income', 'opening'] as const;
export type Direction = 'up' | 'down' | 'flat';

/** A kind the server added later reads as its raw name rather than breaking the page (as subscriptions' kindText). */
export function kindText(t: TFunction, kind: string): string {
  const known = KINDS.find((k) => k === kind);
  return known ? t(`investments.kind.${known}`) : kind;
}

/** The gain's sign read from its decimal string, never parsed to a number. */
export function directionOf(amount: string): Direction {
  if (/^-/.test(amount)) return /^-0*(\.0*)?$/.test(amount) ? 'flat' : 'down';
  return /^0*(\.0*)?$/.test(amount) ? 'flat' : 'up';
}

export const isTransferLeg = (flow: { category?: string | null }) => flow.category === 'transfer';

/** A transfer leg reads as « Virement »; a flow kind the server added later reads as its raw name, as kindText. */
export function flowKindText(t: TFunction, flow: { kind: string; category?: string | null }): string {
  if (isTransferLeg(flow)) return t('investments.flow_kind.transfer');
  const known = FLOW_KINDS.find((k) => k === flow.kind);
  return known ? t(`investments.flow_kind.${known}`) : flow.kind;
}
