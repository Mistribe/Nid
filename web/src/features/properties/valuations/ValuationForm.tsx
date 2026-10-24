import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useRef } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { LOAN_AMOUNT } from '../../loans/new/loanForm';
import { presentRefusal } from '../../loans/refusal';
import { ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateProperties, VALUATIONS, type PropertyDetail } from '../api';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/properties';
import '@/locales/forms';

const PATHS = { asOf: 'asOf', amount: 'amount', note: 'note' } as const;

// The server's bounds: dated on or before today, positive, two decimals (spec 3C1 §3).
const schema = (t: TFunction) => z.object({
  asOf: z.string().regex(ISO_DATE, t('edit.required')).refine((v) => v <= todayIso(), t('field_error.core_date_in_future')),
  amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});

type ValuationValues = z.infer<ReturnType<typeof schema>>;

/**
 * 3D1 design §6: an estimate added in a dialog — a date, a value, a note. On a date already estimated it replaces that one,
 * and the form says so before sending. A save hands the dialog back (`close()`) and the page announces it and places the
 * focus; « Annuler » and Escape just call `onClose`.
 */
export function ValuationDialog({ property, onClose, onDone }: { property: PropertyDetail; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={t('property_valuations.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <ValuationForm property={property} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function ValuationForm({ property, titleId, onDone }: { property: PropertyDetail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<ValuationValues>({ resolver: zodResolver(schema(t)), defaultValues: { asOf: todayIso(), amount: '', note: '' } });
  const errors = useSubmitErrors(form.setError, PATHS);
  const add = $api.useMutation('post', VALUATIONS);
  const replaces = property.valuations.some((v) => v.asOf === form.watch('asOf'));

  const submit = form.handleSubmit((values) => {
    errors.clear();
    add.mutate(
      { params: { path: { id: property.id } }, body: { asOf: values.asOf, amount: toWireAmount(values.amount), ...(values.note.trim() ? { note: values.note } : {}) } },
      {
        onSuccess: () => {
          void invalidateProperties(queryClient);
          onDone(replaces ? t('property_valuations.replaced') : t('property_valuations.added'));
        },
        onError: (error) => errors.onError(error),
      },
    );
  });

  const e = form.formState.errors;
  const problem = errors.problem ? presentRefusal(t, add.error) : null;

  return (
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? <ProblemMessage problem={problem} /> : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="asOf" render={({ field }) => (
          <Field label={t('property_valuations.date')} hint={replaces ? t('property_valuations.replaces') : undefined} error={e.asOf?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} max={todayIso()} />}
          </Field>
        )} />
        <Field label={t('property_valuations.value')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      <Field label={t('property_valuations.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending}>{t('property_valuations.submit')}</Button>
      </div>
    </form>
  );
}
