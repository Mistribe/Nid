import { toPlot } from '@/features/investments/chart/plot';

/** A sort key for an amount the server may not give: the missing ones sort first. A key only, never shown. */
export const amountKey = (amount: string | null | undefined): number => (amount ? toPlot(amount) : Number.NEGATIVE_INFINITY);
