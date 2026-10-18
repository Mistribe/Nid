import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
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

const PURCHASES = '/api/v1/subscriptions/{id}/purchases' as const;
const UNITS = ['day', 'week', 'month', 'year'] as const;
const KINDS = ['prepaid_term', 'gift'] as const;

/**
 * A purchase that grants time to this subscription (1E2a design §4): a prepaid term paid by the household, or a gift
 * card from outside. Bundles and the other offer types are 1E2b's.
 */
export function PurchaseForm({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const schema = z.object({
    label: z.string().trim().min(1, t('edit.required')),
    date: z.string().regex(ISO_DATE, t('edit.invalid_date')),
    kind: z.enum(KINDS),
    amount: z.string().trim().regex(AMOUNT, t('edit.invalid_amount')),
    currency: z.string().trim().toUpperCase().regex(CURRENCY, t('edit.invalid_currency')),
    count: z.string().regex(/^[1-9]\d*$/, t('edit.invalid_count')),
    unit: z.enum(UNITS),
  });
  const currency = detail.phases.at(-1)?.price.currency ?? 'EUR';
  const empty = { label: '', date: '', kind: 'prepaid_term' as const, amount: '', currency, count: '1', unit: 'month' as const };
  const form = useForm({ resolver: zodResolver(schema), defaultValues: empty });
  const errors = useSubmitErrors(form.setError, {
    date: 'date', label: 'label', kind: 'kind', 'price.amount': 'amount', 'price.currency': 'currency', price: 'amount',
    'grants[0].duration.count': 'count', 'grants[0].duration.unit': 'unit', 'grants[0].duration': 'count',
  });
  const add = $api.useMutation('post', PURCHASES, {
    onSuccess: () => { setSaved(true); errors.clear(); form.reset(empty); void invalidateSubscriptions(queryClient); },
    onError: (error) => { setSaved(false); errors.onError(error); },
  });

  const submit = form.handleSubmit((v) => {
    const gift = v.kind === 'gift';
    setSaved(false);
    add.mutate({
      params: { path: { id: detail.id } },
      body: {
        date: v.date, label: v.label, kind: v.kind,
        price: { amount: toWireAmount(v.amount), currency: v.currency },
        payer: { type: gift ? 'external' : 'household', personId: null },
        grants: [{ duration: { count: Number(v.count), unit: v.unit }, source: gift ? 'gift' : 'paid', startsOn: null }],
        allocatedAmount: null, note: null,
      },
    });
  });

  const e = form.formState.errors;
  return (
    <form aria-label={t('edit.purchase_title')} onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3" noValidate>
      <h3 className="font-semibold">{t('edit.purchase_title')}</h3>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      {saved ? <p role="status">{t('edit.saved')}</p> : null}
      <Field label={t('edit.purchase_label')} error={e.label?.message}>
        {(c) => <input {...c} {...form.register('label')} className="w-full rounded-md border px-3 py-2" />}
      </Field>
      <Field label={t('edit.purchase_date')} error={e.date?.message}>
        {(c) => <input type="date" {...c} {...form.register('date')} />}
      </Field>
      <Field label={t('edit.purchase_kind')}>
        {(c) => (
          <NativeSelect {...c} {...form.register('kind')}>
            {KINDS.map((k) => <NativeSelectOption key={k} value={k}>{t(`purchase_kind.${k}`)}</NativeSelectOption>)}
          </NativeSelect>
        )}
      </Field>
      <div className="flex gap-3">
        <Field label={t('edit.purchase_amount')} error={e.amount?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className="w-32 rounded-md border px-3 py-2" />}
        </Field>
        <Field label={t('edit.currency')} error={e.currency?.message}>
          {(c) => <input maxLength={3} {...c} {...form.register('currency')} className="w-20 rounded-md border px-3 py-2 uppercase" />}
        </Field>
      </div>
      <div className="flex gap-3">
        <Field label={t('edit.purchase_duration')} error={e.count?.message}>
          {(c) => <input inputMode="numeric" {...c} {...form.register('count')} className="w-20 rounded-md border px-3 py-2" />}
        </Field>
        <Field label={t('edit.purchase_unit')}>
          {(c) => (
            <NativeSelect {...c} {...form.register('unit')}>
              {UNITS.map((u) => <NativeSelectOption key={u} value={u}>{t(`edit.unit.${u}`)}</NativeSelectOption>)}
            </NativeSelect>
          )}
        </Field>
      </div>
      <Button type="submit" disabled={add.isPending}>{t('edit.purchase_submit')}</Button>
    </form>
  );
}
