import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { trimAmount } from '../investments/entries/entries';
import { ISO_DATE } from '../subscriptions/edits/amount';
import { Field } from '../subscriptions/edits/Field';
import { Row, errorOf, input, sameAmount, wireAmount, type KindUi, type Money } from './kindParts';

type InvoicePayload = { chargedOn: string; amount: Money };
type InvoicePreview = { current: { price: Money } | null; proposed: { price: Money } | null; startsOn: string | null };
type InvoiceForm = { chargedOn: string; amount: string };

const asInvoice = (payload: unknown) => payload as InvoicePayload;

/** Zero is fine (a subscription can become free); a negative price is not. */
const NON_NEGATIVE_AMOUNT = /^\d+([.,]\d{1,4})?$/;

const FIELDS: Record<string, keyof InvoiceForm> = {
  'payload.chargedOn': 'chargedOn', 'payload.amount.amount': 'amount', 'payload.amount.currency': 'amount',
};

function InvoicePreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as InvoicePreview;
  const shown = (m: Money | null | undefined) => (m ? formatMoney(m) : t('proposal.preview.none'));
  return (
    <table className="w-full">
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <tbody>
        <Row label={t('proposal.invoice.current')}>{shown(p.current?.price)}</Row>
        <Row label={t('proposal.invoice.proposed')}>{shown(p.proposed?.price)}</Row>
        <Row label={t('proposal.invoice.starts_on')}>{p.startsOn ? formatLocalDate(p.startsOn) : t('proposal.preview.none')}</Row>
      </tbody>
    </table>
  );
}

function InvoiceFields({ form, original }: { form: UseFormReturn<FieldValues>; original: unknown }) {
  const { t } = useTranslation();
  return (
    <>
      <Field label={t('proposal.invoice.starts_on')} error={errorOf(form, 'chargedOn')}>{(c) => <input type="date" {...c} {...form.register('chargedOn')} className={input} />}</Field>
      <Field label={t('proposal.invoice.amount', { currency: asInvoice(original).amount.currency })} error={errorOf(form, 'amount')}>{(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={input} />}</Field>
    </>
  );
}

export const invoice: KindUi = {
  label: 'proposals.kind.invoice',
  summary: (t, p) => {
    const v = asInvoice(p.editedPayload ?? p.payload);
    return t('proposals.summary.invoice', {
      name: p.targetName ?? t('proposals.unknown_target'), date: formatLocalDate(v.chargedOn, 'short'), amount: formatMoney(v.amount),
    });
  },
  Preview: InvoicePreviewView,
  schema: (t) => z.object({
    chargedOn: z.string().regex(ISO_DATE, t('edit.required')),
    amount: z.string().regex(NON_NEGATIVE_AMOUNT, t('proposal.invoice.amount_format')),
  }),
  toForm: (original) => {
    const o = asInvoice(original);
    return { chargedOn: o.chargedOn, amount: trimAmount(o.amount.amount) };
  },
  toPayload: (values, original) => {
    const v = values as InvoiceForm;
    const o = asInvoice(original);
    if (v.chargedOn === o.chargedOn && sameAmount(v.amount, o.amount.amount)) return null;
    return { chargedOn: v.chargedOn, amount: { amount: wireAmount(v.amount), currency: o.amount.currency } };
  },
  field: (path) => FIELDS[path],
  Fields: InvoiceFields,
  link: (p) => ({ to: '/subscriptions/$id', params: { id: p.targetItemId } }),
};
