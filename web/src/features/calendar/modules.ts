/**
 * Issue #243: the modules « À venir » lists events of, in display order, as the server's `CalendarModules`; each one's label is its
 * navigation entry's (`nav.<module>`).
 */
export const CALENDAR_MODULES = ['subscriptions', 'loans', 'properties', 'investments', 'income', 'tax'] as const;

export type CalendarModule = (typeof CALENDAR_MODULES)[number];

export const moduleLabelKey = (module: CalendarModule) => `nav.${module}` as const;
