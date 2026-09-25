import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { DETAIL, invalidateSubscriptions, type Detail } from '../api';
import { ProviderPicker } from '../ProviderPicker';
import { ISO_DATE } from './amount';
import { Field } from './Field';
import { Reload } from './Reload';
import { useSubmitErrors } from './useSubmitErrors';

const PATHS = { autoRenew: 'autoRenew', cancelledAt: 'cancelledAt', note: 'note', providerKey: 'provider' } as const;

/** Auto-renew, cancellation, note and provider (1E2a design §4): only what changed is sent. */
export function SettingsForm({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [saved, setSaved] = useState(false);
  const schema = z.object({
    autoRenew: z.boolean(),
    cancelledAt: z.string().refine((v) => v === '' || ISO_DATE.test(v), t('edit.invalid_date')),
    note: z.string(),
    provider: z.object({ key: z.string(), name: z.string() }).nullable(),
  });
  const initial = {
    autoRenew: detail.autoRenew,
    cancelledAt: detail.cancelledAt ?? '',
    note: detail.note ?? '',
    provider: detail.provider ? { key: detail.provider.key, name: detail.provider.name } : null,
  };
  const form = useForm({ resolver: zodResolver(schema), defaultValues: initial });
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('patch', DETAIL, {
    onSuccess: () => { setSaved(true); errors.clear(); void invalidateSubscriptions(queryClient); },
    onError: (error) => { setSaved(false); errors.onError(error); },
  });

  const submit = form.handleSubmit((values) => {
    const body = {
      clearCancelledAt: values.cancelledAt === '' && initial.cancelledAt !== '',
      clearProviderKey: values.provider === null && initial.provider !== null,
      ...(values.autoRenew !== initial.autoRenew ? { autoRenew: values.autoRenew } : {}),
      ...(values.cancelledAt !== '' && values.cancelledAt !== initial.cancelledAt ? { cancelledAt: values.cancelledAt } : {}),
      ...(values.note !== initial.note ? { note: values.note } : {}),
      ...(values.provider && values.provider.key !== initial.provider?.key ? { providerKey: values.provider.key } : {}),
    };
    setSaved(false);
    save.mutate({ params: { path: { id: detail.id } }, body });
  });

  return (
    <form aria-label={t('edit.settings_title')} onSubmit={(e) => void submit(e)} className="max-w-md space-y-3" noValidate>
      <h3 className="font-semibold">{t('edit.settings_title')}</h3>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      {saved ? <p role="status">{t('edit.saved')}</p> : null}
      <Field label={t('edit.auto_renew')} inline>{(c) => <input type="checkbox" {...c} {...form.register('autoRenew')} />}</Field>
      <Field label={t('edit.cancelled_at')} error={form.formState.errors.cancelledAt?.message}>
        {(c) => <input type="date" {...c} {...form.register('cancelledAt')} className="rounded-md border px-3 py-2" />}
      </Field>
      <Controller control={form.control} name="provider" render={({ field, fieldState }) => (
        <ProviderPicker label={t('edit.provider')} value={field.value} onChange={field.onChange} error={fieldState.error?.message} />
      )} />
      <Field label={t('edit.note')} error={form.formState.errors.note?.message}>
        {(c) => <textarea {...c} {...form.register('note')} className="w-full rounded-md border px-3 py-2" />}
      </Field>
      <Button type="submit" disabled={save.isPending}>{t('edit.save_settings')}</Button>
    </form>
  );
}
