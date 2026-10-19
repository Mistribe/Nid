import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { invalidateSubscriptions, type Detail } from '../api';
import { AMOUNT, CURRENCY, ISO_DATE, toWireAmount } from './amount';
import { Field } from '@/components/form/Field';
import { Reload } from './Reload';
import { useSubmitErrors } from './useSubmitErrors';

const PRICE_CHANGES = '/api/v1/subscriptions/{id}/price-changes' as const;
const PATHS = { startsOn: 'startsOn', 'price.amount': 'amount', 'price.currency': 'currency', price: 'amount' } as const;

/** 1E2a design §2.3: the open phase ends and the new price starts on one date, in one request, in a dialog. */
export function PriceChangeForm({ detail, titleId, onDone }: { detail: Detail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const schema = z.object({
    startsOn: z.string().regex(ISO_DATE, t('edit.invalid_date')),
    amount: z.string().trim().regex(AMOUNT, t('edit.invalid_amount')),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('edit.invalid_currency')),
  });
  const currency = detail.phases.at(-1)?.price.currency ?? 'EUR';
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { startsOn: '', amount: '', currency } });
  const errors = useSubmitErrors(form.setError, PATHS);
  const change = $api.useMutation('post', PRICE_CHANGES, {
    onSuccess: () => { void invalidateSubscriptions(queryClient); onDone(t('edit.saved')); },
    onError: (error) => errors.onError(error),
  });

  const submit = form.handleSubmit((values) => {
    errors.clear();
    change.mutate({
      params: { path: { id: detail.id } },
      body: { startsOn: values.startsOn, price: { amount: toWireAmount(values.amount), currency: values.currency } },
    });
  });

  const e = form.formState.errors;
  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      <Controller control={form.control} name="startsOn" render={({ field }) => (
        <Field label={t('edit.price_starts')} error={e.startsOn?.message}>
          {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
        </Field>
      )} />
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('edit.price_amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
        <Field label={t('edit.currency')} error={e.currency?.message}>
          {(c) => <Input maxLength={3} {...c} {...form.register('currency')} className="uppercase" />}
        </Field>
      </div>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={change.isPending}>{t('edit.price_submit')}</Button>
      </div>
    </form>
  );
}
