import type { TFunction } from 'i18next';
import type { JSX, ReactNode } from 'react';
import type { FieldValues, UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { z } from 'zod';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { trimAmount } from '../investments/entries/entries';
import type { ProposalView } from './api';

export type Money = { amount: string; currency: string };

export type LinkTarget = { to: '/investments/$id' | '/income/$id' | '/subscriptions/$id'; params: { id: string } };

/** Spec 4D1 §5: what a kind brings to « À valider ». Each kind reads its own payload and preview. */
export type KindUi = {
  /** An i18n key. */
  label: 'proposals.kind.valuation' | 'proposals.kind.statement';
  summary: (t: TFunction, p: ProposalView) => string;
  Preview: (props: { preview: unknown }) => JSX.Element;
  schema: (t: TFunction, original: unknown) => z.ZodType<FieldValues, FieldValues>;
  toForm: (original: unknown, preview: unknown) => FieldValues;
  /** The payload to send as `editedPayload`, or null when nothing really changed (4C2 Review Focus 3). */
  toPayload: (values: FieldValues, original: unknown, preview: unknown) => unknown;
  /** The form field a server error path belongs to; anything else shows above the form. */
  field: (path: string) => string | undefined;
  Fields: (props: { form: UseFormReturn<FieldValues>; original: unknown; preview: unknown }) => JSX.Element;
  link: (p: ProposalView) => LinkTarget;
};

export const input = 'rounded-md border px-3 py-2';

/** The decimal typed, as the wire wants it (a comma read as a point). */
export const wireAmount = (typed: string) => typed.trim().replace(',', '.');

/** The same amount when the trimmed decimal strings match: never arithmetic. */
export const sameAmount = (typed: string, stored: string) => trimAmount(wireAmount(typed)) === trimAmount(stored);

/** A field's message, whatever the form's value type. */
export function errorOf(form: UseFormReturn<FieldValues>, name: string): string | undefined {
  const message = form.formState.errors[name]?.message;
  return typeof message === 'string' ? message : undefined;
}

/** One labelled line of a comparison. */
export function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <tr className="border-b">
      <th scope="row" className="py-2 pr-4 text-left font-normal">{label}</th>
      <td className="py-2 text-right">{children}</td>
    </tr>
  );
}

type Dated = { asOf: string; amount: Money };

/** A value next to the one recorded at that date and the one before (the valuation; the statement's value). */
export function ValueComparison({ preview }: { preview: unknown }) {
  const { t } = useTranslation();
  const p = preview as { current?: Dated | null; previous?: Dated | null; proposed: Dated };
  const row = (label: string, v: Dated | null | undefined) => (
    <tr className="border-b">
      <th scope="row" className="py-2 pr-4 text-left font-normal">{label}</th>
      <td className="py-2 pr-4">{v ? formatLocalDate(v.asOf) : t('proposal.preview.none')}</td>
      <td className="py-2 text-right">{v ? formatMoney(v.amount) : ''}</td>
    </tr>
  );
  return (
    <table className="w-full">
      <caption className="sr-only">{t('proposal.preview.title')}</caption>
      <tbody>
        {row(t('proposal.preview.current'), p.current)}
        {row(t('proposal.preview.previous'), p.previous)}
        {row(t('proposal.preview.proposed'), p.proposed)}
      </tbody>
    </table>
  );
}
