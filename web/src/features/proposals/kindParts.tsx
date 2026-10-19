import type { TFunction } from 'i18next';
import type { JSX, ReactNode } from 'react';
import { Controller, type FieldValues, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { z } from 'zod';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@/components/ui/table';
import type { FieldError, Problem } from '@/api/errors';
import { trimAmount } from '../investments/entries/entries';
import type { ProposalView } from './api';

export type Money = { amount: string; currency: string };

export type LinkTarget = { to: '/investments/$id' | '/income/$id' | '/subscriptions/$id' | '/loans/$id' | '/properties/$id'; params: { id: string } };

/** Spec 4D1 §5: what a kind brings to « À valider ». Each kind reads its own payload and preview. */
export type KindUi = {
  /** An i18n key. */
  label: `proposals.kind.${'valuation' | 'statement' | 'payslip' | 'invoice' | 'amortization'}`;
  summary: (t: TFunction, p: ProposalView) => string;
  Preview: (props: { preview: unknown }) => JSX.Element;
  schema: (t: TFunction, original: unknown) => z.ZodType<FieldValues, FieldValues>;
  toForm: (original: unknown, preview: unknown) => FieldValues;
  /** The payload to send as `editedPayload`, or null when nothing really changed (4C2 Review Focus 3). */
  toPayload: (values: FieldValues, original: unknown, preview: unknown) => unknown;
  /** The form field a server error path belongs to; anything else shows above the form. */
  field: (path: string) => string | undefined;
  /** A server field error worded for this kind, when the shared `field_error.*` text does not fit it. */
  message?: (t: TFunction, error: FieldError) => string | undefined;
  /** The French text of a refused approval of this kind, when the server's own sentence must not be shown. */
  refusal?: (t: TFunction, problem: Problem) => string | undefined;
  Fields: (props: { form: UseFormReturn<FieldValues>; original: unknown; preview: unknown }) => JSX.Element;
  link: (p: ProposalView) => LinkTarget;
  /** True when approving would record nothing: the approval is then disabled. */
  nothing?: (values: FieldValues, original: unknown) => boolean;
};

/** The decimal typed, as the wire wants it (a comma read as a point). */
export const wireAmount = (typed: string) => typed.trim().replace(',', '.');

/** The same amount when the trimmed decimal strings match: never arithmetic. */
export const sameAmount = (typed: string, stored: string) => trimAmount(wireAmount(typed)) === trimAmount(stored);

/** A field's message, whatever the form's value type. */
export function errorOf(form: UseFormReturn<FieldValues>, name: string): string | undefined {
  const message = form.formState.errors[name]?.message;
  return typeof message === 'string' ? message : undefined;
}

/** A date field of a kind's form: typed `jj/mm/aaaa` or picked, the zod schema of the kind judges the text. */
export function DateInput({ form, name, label }: { form: UseFormReturn<FieldValues>; name: string; label: string }) {
  return (
    <Controller control={form.control} name={name} render={({ field }) => (
      <Field label={label} error={errorOf(form, name)}>
        {(c) => <DateField {...c} value={String(field.value ?? '')} onChange={field.onChange} />}
      </Field>
    )} />
  );
}

/** One labelled line of a comparison. */
export function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <TableRow>
      <TableHead scope="row" className="font-normal">{label}</TableHead>
      <TableCell className="text-right">{children}</TableCell>
    </TableRow>
  );
}

type Dated = { asOf: string; amount: Money };

/** A value next to the one recorded at that date and the one before (the valuation; the statement's value). */
export function ValueComparison({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as { current?: Dated | null; previous?: Dated | null; proposed: Dated };
  const row = (label: string, v: Dated | null | undefined) => (
    <TableRow>
      <TableHead scope="row" className="font-normal">{label}</TableHead>
      <TableCell>{v ? formatLocalDate(v.asOf) : t('proposal.preview.none')}</TableCell>
      <TableCell className="text-right">{v ? formatMoney(v.amount) : ''}</TableCell>
    </TableRow>
  );
  return (
    <Table>
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <TableBody>
        {row(t('proposal.preview.current'), p.current)}
        {row(t('proposal.preview.previous'), p.previous)}
        {row(t('proposal.preview.proposed'), p.proposed)}
      </TableBody>
    </Table>
  );
}
