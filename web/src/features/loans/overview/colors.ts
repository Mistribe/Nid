import { NEUTRAL_COLOR } from '@/components/ProviderIcon';

/**
 * Loans dashboard §3.3: a loan keeps one colour across the overview, by its rank in the server's rows (largest balance first),
 * slots 1 to 8 of dataviz.md §3; a ninth loan and the ones after take the neutral tone, never a ninth hue.
 */
export const loanColor = (rank: number): string => (rank >= 0 && rank < 8 ? `var(--chart-${rank + 1})` : NEUTRAL_COLOR);
