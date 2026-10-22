import { NEUTRAL_COLOR } from '@/components/ProviderIcon';

/** dataviz.md §5, « Biens, par type »: a fixed colour per type of property (dashboard spec D8), never by rank. */
const SLOTS: Record<string, number> = { primary_residence: 1, rental: 2, secondary_residence: 3, parking: 4 };

export const typeColor = (type: string): string => (SLOTS[type] ? `var(--chart-${SLOTS[type]})` : NEUTRAL_COLOR);
