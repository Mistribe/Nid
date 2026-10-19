import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { POSITIVE_AMOUNT, trimAmount } from '../investments/entries/entries';
import { ISO_DATE } from '../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { Table, TableBody } from '@/components/ui/table';
import { Input } from '@/components/ui/input';
import { DateInput, Row, errorOf, sameAmount, wireAmount, type KindUi, type Money } from './kindParts';

type Pay = { net: Money; gross?: Money | null };
type PayslipPayload = Pay & { paidOn: string; phaseLabel: string; effectiveOn?: string };
type PayslipPreview = { expected: Pay | null; proposed: Pay | null; effectiveOn: string | null };
type PayslipForm = { effectiveOn: string; net: string; gross: string };

const asPayslip = (payload: unknown) => payload as PayslipPayload;

const FIELDS: Record<string, keyof PayslipForm> = {
  'payload.net.amount': 'net', 'payload.net.currency': 'net', 'payload.gross.amount': 'gross', 'payload.gross.currency': 'gross',
  'payload.effectiveOn': 'effectiveOn', 'payload.paidOn': 'effectiveOn',
};

function PayslipPreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as PayslipPreview;
  const shown = (m: Money | null | undefined) => (m ? formatMoney(m) : t('proposal.preview.none'));
  return (
    <Table>
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <TableBody>
        <Row label={t('proposal.payslip.expected_net')}>{shown(p.expected?.net)}</Row>
        <Row label={t('proposal.payslip.proposed_net')}>{shown(p.proposed?.net)}</Row>
        {p.expected?.gross || p.proposed?.gross ? (
          <>
            <Row label={t('proposal.payslip.expected_gross')}>{shown(p.expected?.gross)}</Row>
            <Row label={t('proposal.payslip.proposed_gross')}>{shown(p.proposed?.gross)}</Row>
          </>
        ) : null}
        <Row label={t('proposal.payslip.effective_on')}>{p.effectiveOn ? formatLocalDate(p.effectiveOn) : t('proposal.preview.none')}</Row>
      </TableBody>
    </Table>
  );
}

function PayslipFields({ form, original }: { form: UseFormReturn<FieldValues>; original: unknown }) {
  const { t } = useTranslation();
  const currency = asPayslip(original).net.currency;
  return (
    <>
      <DateInput form={form} name="effectiveOn" label={t('proposal.payslip.effective_on')} />
      <Field label={t('proposal.payslip.net', { currency })} error={errorOf(form, 'net')}>{(c) => <Input inputMode="decimal" {...c} {...form.register('net')} />}</Field>
      <Field label={t('proposal.payslip.gross', { currency })} error={errorOf(form, 'gross')}>{(c) => <Input inputMode="decimal" {...c} {...form.register('gross')} />}</Field>
    </>
  );
}

export const payslip: KindUi = {
  label: 'proposals.kind.payslip',
  summary: (t, p) => {
    const v = asPayslip(p.editedPayload ?? p.payload);
    return t('proposals.summary.payslip', {
      name: p.targetName ?? t('proposals.unknown_target'), date: formatLocalDate(v.paidOn, 'short'), amount: formatMoney(v.net),
    });
  },
  Preview: PayslipPreviewView,
  schema: (t) => z.object({
    effectiveOn: z.string().regex(ISO_DATE, t('edit.required')),
    net: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
    gross: z.union([z.literal(''), z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format'))]),
  }),
  toForm: (original) => {
    const o = asPayslip(original);
    return { effectiveOn: o.effectiveOn ?? '', net: trimAmount(o.net.amount), gross: o.gross ? trimAmount(o.gross.amount) : '' };
  },
  toPayload: (values, original) => {
    const v = values as PayslipForm;
    const o = asPayslip(original);
    const gross = v.gross.trim();
    const sameGross = o.gross ? sameAmount(gross, o.gross.amount) : gross === '';
    if (v.effectiveOn === (o.effectiveOn ?? '') && sameAmount(v.net, o.net.amount) && sameGross) return null;
    return {
      paidOn: o.paidOn, phaseLabel: o.phaseLabel, net: { amount: wireAmount(v.net), currency: o.net.currency },
      ...(gross ? { gross: { amount: wireAmount(gross), currency: o.net.currency } } : {}),
      effectiveOn: v.effectiveOn,
    };
  },
  field: (path) => FIELDS[path],
  // The income screens' generic text says nothing of a payslip's date.
  message: (t, error) => (error.code === 'income.invalid_phase' ? t('field_error.income_invalid_phase', { context: 'payslip' }) : undefined),
  Fields: PayslipFields,
  link: (p) => ({ to: '/income/$id', params: { id: p.targetItemId } }),
};
