import type { TFunction } from 'i18next';
import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { trimAmount, valueSchema, type ValueFormValues } from '../investments/entries/entries';
import { Field } from '../subscriptions/edits/Field';
import type { ProposalView } from './api';
import { ValueComparison, errorOf, input, sameAmount, wireAmount, type KindUi, type Money } from './kindParts';
import { invoice } from './invoiceKind';
import { payslip } from './payslipKind';
import { statement } from './statementKind';

export type { KindUi } from './kindParts';

type ValuationPayload = { asOf: string; amount: Money; note?: string | null };

const asValuation = (payload: unknown) => payload as ValuationPayload;

const VALUATION_FIELDS: Record<string, keyof ValueFormValues> = {
  'payload.asOf': 'asOf', 'payload.amount.amount': 'amount', 'payload.amount.currency': 'amount', 'payload.note': 'note',
  'editedPayload': 'amount', // ProposalUseCases emits it when the edited payload is not an object
};

function ValuationFields({ form, original }: { form: UseFormReturn<FieldValues>; original: unknown }) {
  const { t } = useTranslation();
  return (
    <>
      <Field label={t('proposal.edit.as_of')} error={errorOf(form, 'asOf')}>{(c) => <input type="date" {...c} {...form.register('asOf')} className={input} />}</Field>
      <Field label={t('proposal.edit.amount', { currency: asValuation(original).amount.currency })} error={errorOf(form, 'amount')}>{(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={input} />}</Field>
      <Field label={t('proposal.edit.note')} error={errorOf(form, 'note')}>{(c) => <input {...c} {...form.register('note')} className={input} />}</Field>
    </>
  );
}

const valuation: KindUi = {
  label: 'proposals.kind.valuation',
  summary: (t, p) => {
    const v = asValuation(p.editedPayload ?? p.payload);
    return t('proposals.summary.valuation', {
      name: p.targetName ?? t('proposals.unknown_target'), date: formatLocalDate(v.asOf, 'short'), amount: formatMoney(v.amount),
    });
  },
  Preview: ValueComparison,
  schema: (t) => valueSchema(t),
  toForm: (payload) => {
    const v = asValuation(payload);
    return { asOf: v.asOf, amount: trimAmount(v.amount.amount), note: v.note ?? '' };
  },
  toPayload: (values, original) => {
    const v = values as ValueFormValues;
    const o = asValuation(original);
    const note = v.note.trim();
    if (v.asOf === o.asOf && sameAmount(v.amount, o.amount.amount) && note === (o.note ?? '').trim()) return null;
    return { asOf: v.asOf, amount: { amount: wireAmount(v.amount), currency: o.amount.currency }, ...(note ? { note } : {}) };
  },
  field: (path) => VALUATION_FIELDS[path],
  Fields: ValuationFields,
  link: (p) => ({ to: '/investments/$id', params: { id: p.targetItemId } }),
};

const KINDS: Record<string, KindUi> = {
  'investments.record_valuation': valuation,
  'investments.record_statement': statement,
  'income.change_pay': payslip,
  'subscriptions.change_price': invoice,
};

export const kindOf = (kind: string): KindUi | undefined => KINDS[kind];

export function originText(t: TFunction, via: string): string {
  return via === 'ui' || via === 'api' || via === 'mcp' ? t(`proposals.origin.${via}`) : via;
}

const newest = (items: ProposalView[]) => items.reduce((m, p) => (p.createdAt > m ? p.createdAt : m), '');

/** The list's groups: one per statement label, the group with the newest proposal first (« Sans relevé » like any other), each newest first. */
export function groupBySource(items: ProposalView[]): { label: string | null; items: ProposalView[] }[] {
  const groups = new Map<string | null, ProposalView[]>();
  for (const p of items) groups.set(p.sourceLabel ?? null, [...(groups.get(p.sourceLabel ?? null) ?? []), p]);
  return [...groups.entries()]
    .map(([label, list]) => ({ label, items: list }))
    .sort((a, b) => newest(b.items).localeCompare(newest(a.items)));
}
