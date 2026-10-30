import type { TFunction } from 'i18next';
import '@/locales/tax';

const STATUSES = ['taxed', 'exempt', 'loss', 'not_taxable', 'not_estimated'] as const;
const FINDINGS = ['opening_date_unknown', 'other_currency', 'no_value'] as const;
const NOTES = [
  'sale_costs_not_deducted', 'rental_works_flat_only', 'land_surtax_not_applied', 'free_acquisition_fees_flat_not_allowed',
  'pea_closes_under_five_years', 'pea_pre_2018_rates_not_modelled', 'brokerage_scale_option', 'life_insurance_social_levies_already_paid',
  'tontine_locked_until_term', 'life_insurance_visible_contracts_only', 'premiums_before_2017_unknown', 'withdrawals_before_tracking_at_face_value',
  'lines_older_than_value', 'value_stale', 'value_estimated', 'approximate', 'total_not_sum_of_pages',
] as const;
const FEES = ['actual', 'flat', 'none'] as const;
const WORKS = ['actual', 'flat', 'none'] as const;

/** A code the server added later reads as its raw name rather than breaking the page. */
export function saleStatusText(t: TFunction, code: string): string {
  const k = STATUSES.find((c) => c === code);
  return k ? t(`tax.sale.status.${k}`) : code;
}
export function saleFindingText(t: TFunction, code: string): string {
  const k = FINDINGS.find((c) => c === code);
  return k ? t(`tax.sale.finding.${k}`) : code;
}
export function saleNoteText(t: TFunction, code: string): string {
  const k = NOTES.find((c) => c === code);
  return k ? t(`tax.sale.note.${k}`) : code;
}
/** « approximatif », « valeur estimée », « valeur à mettre à jour »: the caveats on an item's figures, in the order they weigh. */
export function saleFlagsText(t: TFunction, item: { approximate: boolean; estimated: boolean; stale: boolean }): string[] {
  return [item.approximate && t('tax.sale.approximate'), item.estimated && t('tax.sale.estimated'), item.stale && t('tax.sale.stale')].filter((f) => typeof f === 'string');
}
export function feesMethodText(t: TFunction, code: string): string {
  const k = FEES.find((c) => c === code);
  return k ? t(`tax.sale.fees_method.${k}`) : code;
}
export function worksMethodText(t: TFunction, code: string): string {
  const k = WORKS.find((c) => c === code);
  return k ? t(`tax.sale.works_method.${k}`) : code;
}
