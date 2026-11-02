import { NEUTRAL_COLOR } from '@/components/ProviderIcon';

/** dataviz.md §5, « Placements, vue détaillée »: a fixed colour per kind of envelope (dashboard spec D10), never by rank. */
const SLOTS: Record<string, number> = { life_insurance: 1, pea: 2, savings: 3, checking: 4, per: 5, brokerage: 6 };

/** The order of the kinds in the stack and the legend: the slots', then the neutral tontine, « Autre » last. */
export const KIND_ORDER = ['life_insurance', 'pea', 'savings', 'checking', 'per', 'brokerage', 'tontine', 'other'] as const;

export const kindColor = (kind: string): string => (SLOTS[kind] ? `var(--chart-${SLOTS[kind]})` : NEUTRAL_COLOR);

/** Spec §5.3: a fixed colour per kind of holding, never by rank; cash and « Autre » in the neutral tone. */
const HOLDING_SLOTS: Record<string, number> = { euro_fund: 3, fund: 7, etf: 1, equity: 2, bond: 5, structured: 4 };

export const holdingKindColor = (kind: string): string => (HOLDING_SLOTS[kind] ? `var(--chart-${HOLDING_SLOTS[kind]})` : NEUTRAL_COLOR);

/** dataviz.md §5, « Placements : revenus passifs »: a fixed colour per nature of income, never by rank; « Autres » neutral. */
const INCOME_SLOTS: Record<string, number> = { dividend: 1, interest: 3, distribution: 7, rent: 2 };

export const incomeColor = (kind: string): string => (INCOME_SLOTS[kind] ? `var(--chart-${INCOME_SLOTS[kind]})` : NEUTRAL_COLOR);
