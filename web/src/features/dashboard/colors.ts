import { NEUTRAL_COLOR } from '@/components/ProviderIcon';

// dataviz.md §5: on the dashboard, persons take 5 to 7 (adults by creation, then dependents); the server ranks them as `slot`.
const PERSON_COLORS = ['var(--chart-5)', 'var(--chart-6)', 'var(--chart-7)'];

export const personColor = (slot: number): string => PERSON_COLORS[slot] ?? NEUTRAL_COLOR;
