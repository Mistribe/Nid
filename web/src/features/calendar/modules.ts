/**
 * Issue #243: the modules « À venir » lists events of, in display order, as the server's `CalendarModules`; each one's label is its
 * navigation entry's (`nav.<module>`).
 */
export const CALENDAR_MODULES = ['subscriptions', 'loans', 'properties', 'investments', 'income', 'tax'] as const;

/** Issue #241: with the amounts on, childcare too: it has expected debits, no event (`CalendarModules.WithAmounts`). */
export const AMOUNT_MODULES = [...CALENDAR_MODULES, 'childcare'] as const;

export type CalendarModule = (typeof CALENDAR_MODULES)[number];
export type AmountModule = (typeof AMOUNT_MODULES)[number];

export const moduleLabelKey = (module: AmountModule) => `nav.${module}` as const;
