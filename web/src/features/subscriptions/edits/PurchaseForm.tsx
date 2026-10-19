import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { invalidateSubscriptions, type Detail } from '../api';
import { AMOUNT, CURRENCY, ISO_DATE, toWireAmount } from './amount';
import { Field } from '@/components/form/Field';
import { Reload } from './Reload';
import { useSubmitErrors } from './useSubmitErrors';

const PURCHASES = '/api/v1/subscriptions/{id}/purchases' as const;
const UNITS = ['day', 'week', 'month', 'year'] as const;
const KINDS = ['prepaid_term', 'gift'] as const;

/**
 * A purchase that grants time to this subscription (1E2a design §4): a prepaid term paid by the household, or a gift
 * card from outside. Bundles and the other offer types are 1E2b's.
 */
export function PurchaseForm({ detail, titleId, onDone }: { detail: Detail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
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
    onSuccess: () => { void invalidateSubscriptions(queryClient); onDone(t('edit.saved')); },
    onError: (error) => errors.onError(error),
  });

  const submit = form.handleSubmit((v) => {
    const gift = v.kind === 'gift';
    errors.clear();
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
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      <Field label={t('edit.purchase_label')} error={e.label?.message}>
        {(c) => <Input {...c} {...form.register('label')} />}
      </Field>
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="date" render={({ field }) => (
          <Field label={t('edit.purchase_date')} error={e.date?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <Controller control={form.control} name="kind" render={({ field }) => (
          <Field label={t('edit.purchase_kind')}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={KINDS.map((k) => ({ value: k, label: t(`purchase_kind.${k}`) }))} />}
          </Field>
        )} />
        <Field label={t('edit.purchase_amount')} error={e.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
        </Field>
        <Field label={t('edit.currency')} error={e.currency?.message}>
          {(c) => <Input maxLength={3} {...c} {...form.register('currency')} className="uppercase" />}
        </Field>
        <Field label={t('edit.purchase_duration')} error={e.count?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('count')} />}
        </Field>
        <Controller control={form.control} name="unit" render={({ field }) => (
          <Field label={t('edit.purchase_unit')}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={UNITS.map((u) => ({ value: u, label: t(`edit.unit.${u}`) }))} />}
          </Field>
        )} />
      </div>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={add.isPending}>{t('edit.purchase_submit')}</Button>
      </div>
    </form>
  );
}
