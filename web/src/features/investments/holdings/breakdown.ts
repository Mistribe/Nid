import type { TFunction } from 'i18next';
import '@/locales/investments';

/** Holdings exposure D1–D2: the fixed regions and sectors, in display order (« Autres » last). */
export const REGIONS = ['north_america', 'europe_ex_france', 'france', 'japan', 'asia_pacific_ex_japan', 'emerging', 'other'] as const;
export const SECTORS = [
  'information_technology', 'health_care', 'financials', 'industrials', 'consumer_discretionary', 'consumer_staples', 'energy',
  'materials', 'communication_services', 'utilities', 'real_estate', 'other',
] as const;

/** A euro fund, cash and an SCPI's buildings have no market exposure (the server's `investments.breakdown_not_market`). */
export const hasBreakdown = (kind: string) => kind !== 'euro_fund' && kind !== 'cash' && kind !== 'scpi';

/** A share as the server rounded it, one decimal at least (`70` → `70,0`). */
export const sharePct = (value: number) => new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 2 }).format(value);

/** The label of a code, its own text when a newer server sends one this build does not know. */
export const regionText = (t: TFunction, code: string) => { const c = REGIONS.find((r) => r === code); return c ? t(`exposure.region.${c}`) : code; };
export const sectorText = (t: TFunction, code: string) => { const c = SECTORS.find((r) => r === code); return c ? t(`exposure.sector.${c}`) : code; };
