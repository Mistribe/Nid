import type { TFunction } from 'i18next';
import type { MoneyValue } from '@/components/Money';
import { csvAmount, csvText } from '@/lib/csv';
import type { ChildcareYear } from './api';
import '@/locales/childcare';

export type YearLine = { key: string; label: string; hint: string; amount?: MoneyValue; text?: string; box?: string };

/**
 * #411: the year summary's lines in the issue's order: pay, social contributions (covered, or #30 the share left to the household),
 * CMG, upkeep, meals, then the retained cost with its 2042 box. Amounts are the server's.
 */
export function yearLines(t: TFunction, view: ChildcareYear): YearLine[] {
  return [
    { key: 'net_pay', label: t('childcare_year.net_pay'), hint: t('childcare_year.hint.net_pay'), amount: view.netPay },
    view.contributions && !/^0(\.0+)?$/.test(view.contributions.amount)
      ? { key: 'contributions', label: `${t('childcare_year.contributions')} · ${t('childcare_year.contributions_paid')}`, hint: t('childcare_year.hint.contributions'), amount: view.contributions }
      : { key: 'contributions', label: t('childcare_year.contributions'), hint: t('childcare_year.hint.contributions'), text: t('childcare_year.contributions_value') },
    { key: 'cmg', label: t('childcare_year.cmg'), hint: t('childcare_year.hint.cmg'), amount: view.cmg },
    { key: 'upkeep', label: t('childcare_year.upkeep'), hint: t('childcare_year.hint.upkeep'), amount: view.upkeep },
    { key: 'meals', label: t('childcare_year.meals'), hint: t('childcare_year.hint.meals'), amount: view.meals },
    { key: 'retained', label: t('childcare_year.retained'), hint: t('childcare_year.hint.retained'), amount: view.retained, box: view.box ?? undefined },
  ];
}

/** The summary for a spreadsheet: one row per line, then the months left out. */
export function childcareYearCsv(view: ChildcareYear, t: TFunction, month: (m: string) => string): string {
  return csvText([
    [t('childcare_year.csv_header.line'), t('childcare_year.csv_header.box'), t('childcare_year.csv_header.amount')],
    ...yearLines(t, view).map((l) => [l.label, l.box ?? '', l.amount ? csvAmount(l.amount.amount) : l.text ?? '']),
    ...(view.notValidated.length ? [[t('childcare_year.csv_not_validated'), '', view.notValidated.map(month).join(', ')]] : []),
  ]);
}
