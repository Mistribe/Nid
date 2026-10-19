import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { trimAmount } from '../investments/entries/entries';
import { ISO_DATE } from '../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { Table, TableBody } from '@/components/ui/table';
import { Input } from '@/components/ui/input';
import { durationText } from '../subscriptions/format';
import { DateInput, Row, errorOf, sameAmount, wireAmount, type KindUi, type Money } from './kindParts';

type InvoicePayload = { chargedOn: string; amount: Money };
type InvoicePreview = { current: { price: Money; every: { count: number; unit: string } } | null; proposed: { price: Money } | null; startsOn: string | null };
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
    <Table>
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <TableBody>
        <Row label={t('proposal.invoice.current')}>{shown(p.current?.price)}</Row>
        {p.current ? <Row label={t('proposal.invoice.every')}>{durationText(t, p.current.every)}</Row> : null}
        <Row label={t('proposal.invoice.proposed')}>{shown(p.proposed?.price)}</Row>
        <Row label={t('proposal.invoice.starts_on')}>{p.startsOn ? formatLocalDate(p.startsOn) : t('proposal.preview.none')}</Row>
      </TableBody>
    </Table>
  );
}

function InvoiceFields({ form, original }: { form: UseFormReturn<FieldValues>; original: unknown }) {
  const { t } = useTranslation();
  return (
    <>
      <DateInput form={form} name="chargedOn" label={t('proposal.invoice.starts_on')} />
      <Field label={t('proposal.invoice.amount', { currency: asInvoice(original).amount.currency })} error={errorOf(form, 'amount')}>{(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}</Field>
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
  // The subscription screens' « La fin doit venir après le début. » does not fit an invoice's date.
  message: (t, error) => (error.code === 'subscriptions.phase_range' ? t('field_error.subscriptions_phase_range', { context: 'invoice' }) : undefined),
  Fields: InvoiceFields,
  link: (p) => ({ to: '/subscriptions/$id', params: { id: p.targetItemId ?? '' } }),
};
