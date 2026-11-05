import type { TFunction } from 'i18next';

export const KINDS = ['checking', 'savings', 'pea', 'life_insurance', 'per', 'brokerage', 'tontine', 'other'] as const;
/** The kinds taxed as a life insurance contract, which keep the premiums paid before 27/09/2017. */
export const hasPremiumsBefore2017 = (kind: string) => kind === 'life_insurance' || kind === 'tontine';

export const CADENCES = ['monthly', 'quarterly', 'yearly', 'manual'] as const;
/** A savings envelope's regulated livret (issue #399); `none` is a bank's own savings account. */
export const REGULATED = ['none', 'livret_a', 'ldds', 'lep', 'livret_jeune'] as const;
export const regulatedOptions = (t: TFunction) => REGULATED.map((r) => ({ value: r, label: t(`investments.regulated.${r}`) }));
/** The categories that classify a withdrawal or an income as passive income (the server's own list). */
export const INCOME_CATEGORIES = ['dividend', 'interest', 'distribution'] as const;
export const FLOW_KINDS = ['contribution', 'withdrawal', 'expense', 'income', 'opening'] as const;
/** The currencies a response could not convert, from its `core.fx_rate_missing` findings (reference `USD:2025-10-15`). */
export const missingRateCodes = (findings: { code: string; reference?: string | null }[]) =>
  [...new Set(findings.filter((f) => f.code === 'core.fx_rate_missing').map((f) => (f.reference ?? '').split(':')[0]))].join(', ');
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
  const nature = INCOME_CATEGORIES.find((c) => c === flow.category);
  if (nature) return t(`investments.flow_nature.${nature}`);
  const known = FLOW_KINDS.find((k) => k === flow.kind);
  return known ? t(`investments.flow_kind.${known}`) : flow.kind;
}
