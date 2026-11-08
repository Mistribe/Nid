import type { TFunction } from 'i18next';
import { z } from 'zod';
import type { components } from '@/api/schema';
import { ISO_DATE, toFieldAmount, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import type { LoanFee } from '@/api/loans';
import { FEE_KINDS } from '../format';
import { LOAN_AMOUNT } from '../new/loanForm';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/forms';

/** The kinds a new fee is offered (3B2 design §6): a penalty is entered with its repayment. */
export const NEW_FEE_KINDS = ['file', 'guarantee', 'renegotiation', 'other'] as const;

export function feeSchema(t: TFunction) {
  return z.object({
    kind: z.enum(FEE_KINDS),
    amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
    onDate: z.string().regex(ISO_DATE, t('edit.required')),
    note: z.string().max(2000, t('investments_form.note_too_long')),
  });
}

export type FeeValues = z.infer<ReturnType<typeof feeSchema>>;

/** A server path to the field that owns it; `feeId` (a linked penalty) and anything else go above the form. */
export const FEE_PATHS = { kind: 'kind', amount: 'amount', onDate: 'onDate', note: 'note' } as const satisfies Record<string, keyof FeeValues>;

/** A new fee: « Frais de dossier », today; a stored one as the server wrote it. */
export function feeValuesOf(f?: LoanFee): FeeValues {
  // The server writes only its own kinds.
  return f
    ? { kind: f.kind as FeeValues['kind'], amount: toFieldAmount(f.amount), onDate: f.onDate, note: f.note ?? '' }
    : { kind: 'file', amount: '', onDate: todayIso(), note: '' };
}

/** The body: the amount as typed with a dot, an empty note left out; `version` on a correction only. */
export function toFeeBody(v: FeeValues, version?: number): components['schemas']['LoanFeeBody'] {
  return {
    kind: v.kind, amount: toWireAmount(v.amount), onDate: v.onDate,
    ...(v.note.trim() ? { note: v.note } : {}),
    ...(version === undefined ? {} : { version }),
  };
}
