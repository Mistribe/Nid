import { NEUTRAL_COLOR } from '@/components/ProviderIcon';

// dataviz.md §5: on the dashboard, persons take 5 to 7 (adults by creation, then dependents); the server ranks them as `slot`.
const PERSON_COLORS = ['var(--chart-5)', 'var(--chart-6)', 'var(--chart-7)'];

export const personColor = (slot: number): string => PERSON_COLORS[slot] ?? NEUTRAL_COLOR;

// dataviz.md §5, « Répartition » (issue #104): a fixed colour per asset class and per liquidity, never by rank.
const CLASS_SLOTS: Record<string, number> = { money_market: 1, equity: 2, capital_guaranteed: 3, real_estate: 4, bond: 5, cash: 6, mixed: 7 };
const LIQUIDITY_SLOTS: Record<string, number> = { available: 1, days: 3, locked: 7 };

export const classColor = (key: string): string => (CLASS_SLOTS[key] ? `var(--chart-${CLASS_SLOTS[key]})` : NEUTRAL_COLOR);
export const liquidityColor = (key: string): string => (LIQUIDITY_SLOTS[key] ? `var(--chart-${LIQUIDITY_SLOTS[key]})` : NEUTRAL_COLOR);
