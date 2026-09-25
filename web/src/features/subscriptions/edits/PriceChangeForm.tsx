import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { invalidateSubscriptions, type Detail } from '../api';
import { AMOUNT, CURRENCY, ISO_DATE, toWireAmount } from './amount';
import { Field } from './Field';
import { Reload } from './Reload';
import { useSubmitErrors } from './useSubmitErrors';

const PRICE_CHANGES = '/api/v1/subscriptions/{id}/price-changes' as const;
const PATHS = { startsOn: 'startsOn', 'price.amount': 'amount', 'price.currency': 'currency', price: 'amount' } as const;

/** 1E2a design §2.3: the open phase ends and the new price starts on one date, in one request. */
export function PriceChangeForm({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const schema = z.object({
    startsOn: z.string().regex(ISO_DATE, t('edit.invalid_date')),
    amount: z.string().trim().regex(AMOUNT, t('edit.invalid_amount')),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('edit.invalid_currency')),
  });
  const currency = detail.phases.at(-1)?.price.currency ?? 'EUR';
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { startsOn: '', amount: '', currency } });
  const errors = useSubmitErrors(form.setError, PATHS);
  const change = $api.useMutation('post', PRICE_CHANGES, {
    onSuccess: () => { setSaved(true); errors.clear(); form.reset({ startsOn: '', amount: '', currency }); void invalidateSubscriptions(queryClient); },
    onError: (error) => { setSaved(false); errors.onError(error); },
  });

  const submit = form.handleSubmit((values) => {
    setSaved(false);
    change.mutate({
      params: { path: { id: detail.id } },
      body: { startsOn: values.startsOn, price: { amount: toWireAmount(values.amount), currency: values.currency } },
    });
  });

  const e = form.formState.errors;
  return (
    <form aria-label={t('edit.price_title')} onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3" noValidate>
      <h3 className="font-semibold">{t('edit.price_title')}</h3>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      {saved ? <p role="status">{t('edit.saved')}</p> : null}
      <Field label={t('edit.price_starts')} error={e.startsOn?.message}>
        {(c) => <input type="date" {...c} {...form.register('startsOn')} className="rounded-md border px-3 py-2" />}
      </Field>
      <div className="flex gap-3">
        <Field label={t('edit.price_amount')} error={e.amount?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className="w-32 rounded-md border px-3 py-2" />}
        </Field>
        <Field label={t('edit.currency')} error={e.currency?.message}>
          {(c) => <input maxLength={3} {...c} {...form.register('currency')} className="w-20 rounded-md border px-3 py-2 uppercase" />}
        </Field>
      </div>
      <Button type="submit" disabled={change.isPending}>{t('edit.price_submit')}</Button>
    </form>
  );
}
