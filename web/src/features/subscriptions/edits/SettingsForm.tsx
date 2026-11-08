import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { asEditBody, DETAIL, invalidateSubscriptions, type Detail } from '@/api/subscriptions';
import { ProviderPicker } from '../ProviderPicker';
import { ISO_DATE } from '@/lib/amount';
import { Field } from '@/components/form/Field';
import { Reload } from './Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import '@/locales/forms';

const PATHS = { autoRenew: 'autoRenew', cancelledAt: 'cancelledAt', note: 'note', providerKey: 'provider' } as const;

/** Auto-renew, cancellation, note and provider (1E2a design §4): only what changed is sent. */
export function SettingsForm({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const schema = z.object({
    autoRenew: z.boolean(),
    cancelledAt: z.string().refine((v) => v === '' || ISO_DATE.test(v), t('edit.invalid_date')),
    note: z.string(),
    provider: z.object({ key: z.string(), name: z.string() }).nullable(),
  });
  const initial = useMemo(() => ({
    autoRenew: detail.autoRenew,
    cancelledAt: detail.cancelledAt ?? '',
    note: detail.note ?? '',
    provider: detail.provider ? { key: detail.provider.key, name: detail.provider.name } : null,
  }), [detail]);
  const form = useForm({ resolver: zodResolver(schema), defaultValues: initial });
  // A newer version of the subscription (Reload, refetch) refreshes every field the user has not touched (final review).
  useEffect(() => { form.reset(initial, { keepDirtyValues: true }); }, [form, initial]);
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('patch', DETAIL, {
    onSuccess: () => { toast.success(t('edit.saved')); errors.clear(); form.reset(form.getValues()); void invalidateSubscriptions(queryClient); },
    onError: (error) => errors.onError(error),
  });

  const submit = form.handleSubmit((values) => {
    // Only what the user changed: a field left alone is never sent back over someone else's edit (final review).
    const dirty = form.formState.dirtyFields;
    const body = {
      clearCancelledAt: !!dirty.cancelledAt && values.cancelledAt === '',
      clearProviderKey: !!dirty.provider && values.provider === null,
      ...(dirty.autoRenew ? { autoRenew: values.autoRenew } : {}),
      ...(dirty.cancelledAt && values.cancelledAt !== '' ? { cancelledAt: values.cancelledAt } : {}),
      ...(dirty.note ? { note: values.note } : {}),
      ...(dirty.provider && values.provider ? { providerKey: values.provider.key } : {}),
    };
    save.mutate({ params: { path: { id: detail.id } }, body: asEditBody(body) });
  });

  return (
    <form aria-label={t('edit.settings_title')} onSubmit={(e) => void submit(e)} className="max-w-xl space-y-3" noValidate>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      <Controller control={form.control} name="autoRenew" render={({ field }) => (
        <Field label={t('edit.auto_renew')} inline>{(c) => <Switch {...c} checked={field.value} onCheckedChange={field.onChange} />}</Field>
      )} />
      <Controller control={form.control} name="cancelledAt" render={({ field }) => (
        <Field label={t('edit.cancelled_at')} error={form.formState.errors.cancelledAt?.message}>
          {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
        </Field>
      )} />
      <Controller control={form.control} name="provider" render={({ field, fieldState }) => (
        <ProviderPicker label={t('edit.provider')} value={field.value} onChange={field.onChange} error={fieldState.error?.message} />
      )} />
      <Field label={t('edit.note')} error={form.formState.errors.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <Button type="submit" disabled={save.isPending}>{t('edit.save_settings')}</Button>
    </form>
  );
}
