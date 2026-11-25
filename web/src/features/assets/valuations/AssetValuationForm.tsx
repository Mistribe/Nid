import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ASSET_VALUATIONS, invalidateAssets, type AssetView } from '@/api/assets';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { ISO_DATE, POSITIVE_AMOUNT, toWireAmount } from '@/lib/amount';
import { todayIso } from '@/lib/today';
import '@/locales/assets';
import '@/locales/investments';
import '@/locales/loans';
import '@/locales/forms';

const PATHS = { asOf: 'asOf', amount: 'amount', note: 'note' } as const;

// The server's bounds: dated on or before today, positive, two decimals.
const schema = (t: TFunction) => z.object({
  asOf: z.string().regex(ISO_DATE, t('edit.required')).refine((v) => v <= todayIso(), t('field_error.core_date_in_future')),
  amount: z.string().trim().regex(POSITIVE_AMOUNT, t('loan_form.amount_format')),
  note: z.string().max(2000, t('investments_form.note_too_long')),
});
type Values = z.infer<ReturnType<typeof schema>>;

/** A value added in a dialog: a date, an amount, a note. On a date already valued it replaces that value, and says so first. */
export function AssetValuationDialog({ asset, onClose, onDone }: { asset: AssetView; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  return (
    <FormDialog title={t('assets_valuations.add_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => <Form asset={asset} titleId={titleId} done={done} />}
    </FormDialog>
  );
}

function Form({ asset, titleId, done }: { asset: AssetView; titleId: string; done: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<Values>({ resolver: zodResolver(schema(t)), defaultValues: { asOf: todayIso(), amount: '', note: '' } });
  const errors = useSubmitErrors(form.setError, PATHS);
  const add = $api.useMutation('post', ASSET_VALUATIONS);
  const replaces = asset.valuations.some((v) => v.asOf === form.watch('asOf'));
  const e = form.formState.errors;

  const submit = form.handleSubmit((values) => {
    errors.clear();
    add.mutate(
      { params: { path: { id: asset.id } }, body: { asOf: values.asOf, amount: toWireAmount(values.amount), ...(values.note.trim() ? { note: values.note } : {}) } },
      { onSuccess: () => { void invalidateAssets(queryClient); done(t(replaces ? 'assets_valuations.replaced' : 'assets_valuations.added')); }, onError: errors.onError },
    );
  });

  return (
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="asOf" render={({ field }) => (
          <Field label={t('assets_valuations.date')} hint={replaces ? t('assets_valuations.replaces') : undefined} error={e.asOf?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} max={todayIso()} />}
          </Field>
        )} />
        <Field label={t('assets_valuations.amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
      </div>
      <Field label={t('assets_valuations.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={add.isPending}>{t('assets_valuations.submit')}</Button>
      </div>
    </form>
  );
}
