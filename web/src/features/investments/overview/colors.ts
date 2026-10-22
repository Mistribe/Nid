import { NEUTRAL_COLOR } from '@/components/ProviderIcon';

/** dataviz.md §5, « Placements, vue détaillée »: a fixed colour per kind of envelope (dashboard spec D10), never by rank. */
const SLOTS: Record<string, number> = { life_insurance: 1, pea: 2, savings: 3, checking: 4, per: 5, brokerage: 6 };

/** The order of the kinds in the stack and the legend: the slots', « Autre » last. */
export const KIND_ORDER = ['life_insurance', 'pea', 'savings', 'checking', 'per', 'brokerage', 'other'] as const;

export const kindColor = (kind: string): string => (SLOTS[kind] ? `var(--chart-${SLOTS[kind]})` : NEUTRAL_COLOR);
