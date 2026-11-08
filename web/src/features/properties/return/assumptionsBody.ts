import { percentToFraction } from '../../loans/rate';
import { toWireAmount } from '@/lib/amount';
import type { ReturnAssumptionsBody } from '../api';

/** The dialog's fields as typed: percents with a comma, amounts in euros, counts in digits; empty means « by default ». */
export interface AssumptionsForm {
  yearsAfterPinel: string; resalePrice: string; valueGrowth: string; rentGrowth: string; chargesGrowth: string;
  vacancyMonths: string; rentAfterPinel: string; marginalRate: string; resaleFees: string;
}

export const EMPTY: AssumptionsForm = {
  yearsAfterPinel: '', resalePrice: '', valueGrowth: '', rentGrowth: '', chargesGrowth: '', vacancyMonths: '', rentAfterPinel: '', marginalRate: '', resaleFees: '',
};

/** A percent as typed, sign kept, as the API's fraction: text only, never arithmetic. */
const fraction = (typed: string) => {
  const negative = typed.trim().startsWith('-');
  const value = percentToFraction(typed.trim().replace(/^-/, '')) ?? typed.trim();
  return negative && value !== '0' ? `-${value}` : value;
};

/** Property return spec §3: the body of the PUT, empty fields left out so they keep their default. */
export function assumptionsBody(form: AssumptionsForm): ReturnAssumptionsBody {
  const body: ReturnAssumptionsBody = {};
  const set = <K extends keyof AssumptionsForm>(key: K, convert: (typed: string) => string | number) => {
    if (form[key].trim()) (body as Record<string, string | number>)[key] = convert(form[key]);
  };
  set('yearsAfterPinel', Number);
  set('resalePrice', toWireAmount);
  set('valueGrowth', fraction);
  set('rentGrowth', fraction);
  set('chargesGrowth', fraction);
  set('vacancyMonths', Number);
  set('rentAfterPinel', toWireAmount);
  set('marginalRate', (v) => v);
  set('resaleFees', fraction);
  return body;
}
