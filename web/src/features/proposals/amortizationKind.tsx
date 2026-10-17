import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import type { BankRow, LoanComparison } from '../loans/api';
import { Comparison, PreviewRows } from '../loans/bank/BankScheduleSection';
import { Field } from '../subscriptions/edits/Field';
import type { KindUi } from './kindParts';

type AmortizationPayload = { rows: unknown[]; makeReference?: boolean };
type Source = 'computed' | 'bank';
type AmortizationPreview = {
  currency: string;
  summary: LoanComparison | null;
  reference: { current: Source; proposed: Source } | null;
  first: BankRow[];
  last: BankRow[];
  count: number;
  range: { from: string; to: string } | null;
};
type AmortizationForm = { makeReference: boolean };

const asTable = (payload: unknown) => payload as AmortizationPayload;
const asPreview = (preview: unknown) => preview as AmortizationPreview;

/** Spec 4D2 §4: the loan page's comparison and rows (the server's figures, formatted only), the period, the reference today. */
function AmortizationPreviewView({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = asPreview(preview);
  const shown = p.first.length + p.last.length;
  return (
    <div className="space-y-3">
      {p.range ? <p>{t('loan_bank.period', { first: formatLocalDate(p.range.from), last: formatLocalDate(p.range.to), count: p.count })}</p> : null}
      {p.summary ? <Comparison currency={p.currency} comparison={p.summary} /> : null}
      {p.reference ? <p>{t('loan_bank.reference_is', { source: t(p.reference.current === 'bank' ? 'loan_bank.source.bank' : 'loan_bank.source.computed') })}</p> : null}
      {shown > 0 ? (
        <PreviewRows rows={[...p.first, ...p.last]} currency={p.currency}
          gap={p.count > shown && p.last.length > 0 ? { after: p.first.length, count: p.count - shown } : undefined} />
      ) : null}
    </div>
  );
}

/** The rows are not edited; the only choice is the reference, offered while the loan reads its calculation. */
function AmortizationFields({ form, preview }: { form: UseFormReturn<FieldValues>; original: unknown; preview: unknown }) {
  const { t } = useTranslation();
  if (asPreview(preview).reference?.current === 'bank') return <p>{t('proposal.amortization.stays_bank')}</p>;
  return (
    <Field inline label={t('proposal.amortization.make_reference')}>
      {(c) => <input type="checkbox" {...c} {...form.register('makeReference')} />}
    </Field>
  );
}

export const amortization: KindUi = {
  label: 'proposals.kind.amortization',
  summary: (t, p) => t('proposals.summary.amortization', {
    name: p.targetName ?? t('proposals.unknown_target'), count: asTable(p.editedPayload ?? p.payload).rows.length,
  }),
  Preview: AmortizationPreviewView,
  schema: () => z.object({ makeReference: z.boolean() }),
  // Absent, the server makes the table the reference: the box starts checked.
  toForm: (original) => ({ makeReference: asTable(original).makeReference ?? true }),
  toPayload: (values, original) => {
    const o = asTable(original);
    const { makeReference } = values as AmortizationForm;
    return makeReference === (o.makeReference ?? true) ? null : { ...o, makeReference };
  },
  field: () => undefined,
  // The rows are the bank's: whatever the server refuses at them (3F2's rules, a repayment the table makes impossible) is one French sentence.
  refusal: (t, problem) => {
    const rowError = problem.errors?.find((e) => e.code === 'realestate.bank_schedule_invalid' || e.path.startsWith('payload.rows'));
    if (problem.code !== 'realestate.bank_schedule_invalid' && !rowError) return undefined;
    // The first refused row, numbered from 1 as the bank prints it.
    const k = /^payload\.rows\[(\d+)\]/.exec(rowError?.path ?? '')?.[1];
    return k === undefined ? t('proposal.amortization.refused') : t('proposal.amortization.refused', { context: 'row', n: Number(k) + 1 });
  },
  Fields: AmortizationFields,
  link: (p) => ({ to: '/loans/$id', params: { id: p.targetItemId } }),
};
