import type { components } from '@/api/schema';

export type ProjectionComparison = components['schemas']['ProjectionComparisonView'];
export type ProjectionDocument = components['schemas']['ProjectionDocumentView'];
export type ProjectionEvent = components['schemas']['ProjectionEventView'];
export type ProjectionOverrides = components['schemas']['ProjectionOverridesView'];
export type EventDate = components['schemas']['EventDateView'];

/** Spec §4: the kinds of event, in the order the « Ajouter un événement » menu offers them. */
export const EVENT_KINDS = ['income_change', 'savings_change', 'early_repayment', 'property_sale', 'property_purchase'] as const;
export type EventKind = (typeof EVENT_KINDS)[number];

/** The kinds that act on a loan or a property: offered only with the real estate module. */
export const ESTATE_KINDS: readonly EventKind[] = ['early_repayment', 'property_sale'];

export const isEventKind = (kind: string | null | undefined): kind is EventKind => EVENT_KINDS.includes(kind as EventKind);
