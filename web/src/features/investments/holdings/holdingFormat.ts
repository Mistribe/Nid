import type { TFunction } from 'i18next';
import type { HoldingWarning } from '../api';
import { formatMoney } from '@/components/Money';
import { formatLocalDate } from '@/components/LocalDate';

export const HOLDING_KINDS = ['fund', 'etf', 'euro_fund', 'equity', 'bond', 'structured', 'cash', 'other'] as const;
/** The kinds a person records by hand; a switch's two legs go through their own dialog (spec §2.3). */
export const MOVEMENT_KINDS = ['allocation_in', 'allocation_out', 'fee', 'interest', 'income', 'maturity'] as const;
const OUT_KINDS = new Set(['allocation_out', 'switch_out', 'maturity', 'fee']);

/** A euro fund and cash have neither units nor a unit price. */
export const hasUnits = (kind: string) => kind !== 'euro_fund' && kind !== 'cash';

/** The server stores units signed by the movement's kind; the person types them unsigned. A sign, not arithmetic. */
export const signedUnits = (kind: string, units: string) => (OUT_KINDS.has(kind) ? `-${units}` : units);

/** Units and prices as printed, up to 6 decimals, formatted from the decimal string. */
export const formatUnits = (units: string) =>
  new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 6 }).format(units as `${number}`);

/** Units typed with 6 decimals at most, unsigned; an amount with 4 at most (the server's bounds). */
export const UNITS = /^\d+([.,]\d{1,6})?$/;

/** Trailing zeros dropped from units as the server prints them (`10.500000` → `10.5`), string trimming only. */
export const trimUnits = (units: string) => units.replace(/^-/, '').replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');

const known = <T extends string>(list: readonly T[], value: string) => list.find((k) => k === value);

export function holdingKindText(t: TFunction, kind: string): string {
  const k = known(HOLDING_KINDS, kind);
  return k ? t(`holdings.kind.${k}`) : kind;
}

export function movementKindText(t: TFunction, kind: string): string {
  const k = known([...MOVEMENT_KINDS, 'switch_in', 'switch_out'] as const, kind);
  return k ? t(`holdings.movement_kind.${k}`) : kind;
}

/** A warning as one sentence; `names` resolves the holding it is about. An unknown code keeps its raw name. */
export function warningText(t: TFunction, w: HoldingWarning, names: (id: string) => string | undefined): string {
  const values = {
    name: (w.holdingId && names(w.holdingId)) || '',
    date: w.on ? formatLocalDate(w.on) : '',
    units: w.units ? formatUnits(w.units.replace(/^-/, '')) : '',
    fee: w.proposedFee ? formatMoney(w.proposedFee) : '',
  };
  const key = w.code === 'unexplained_units' && w.units?.startsWith('-') ? (w.proposedFee ? 'units_missing_fee' : 'units_missing') : w.code;
  return t(`holdings.warning.${key}`, { ...values, defaultValue: w.code });
}

/** A percentage the server gave or the person stated (a cost report, a weight), up to 4 decimals, unsigned unless negative. */
export const percent = (value: number) => new Intl.NumberFormat('fr-FR', { maximumFractionDigits: 4 }).format(value);
