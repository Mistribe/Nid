import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { LABELS, PROVIDERS } from '@/api/subscriptions';
import { Field } from '@/components/form/Field';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { labelColour } from '@/lib/labelColour';
import '@/locales/forms';

/** The server's bounds (`CatalogLimits`, `ProviderRules`): the server checks them again. */
const MAX_NAME = 100;
const MAX_URL = 2048;

const isHttpUrl = (value: string) => {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
};

/**
 * 1E2b2 design §2.2: a custom provider, created right away and handed back to the picker. A modal dialog in a
 * portal: the wizard is a form, and a form never nests another. React still bubbles `submit` through the portal, so the
 * dialog's form stops it. Closing goes through `finish`, which closes the dialog before the picker moves the focus:
 * the browser's own focus restoration would otherwise run last.
 */
export function NewProviderDialog({ onCreated, onClose }: {
  onCreated: (provider: { key: string; name: string }) => void; onClose: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const ref = useRef<HTMLDialogElement>(null);
  const labelsErrorId = useId();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const labels = $api.useQuery('get', LABELS);

  const schema = z.object({
    name: z.string().trim().min(1, t('provider_new.name_required')).max(MAX_NAME, t('provider_new.name_too_long', { max: MAX_NAME })),
    labelKeys: z.array(z.string()).min(1, t('provider_new.labels_required')),
    website: z.string().trim().max(MAX_URL, t('provider_new.website_too_long', { max: MAX_URL }))
      .refine((value) => value === '' || isHttpUrl(value), t('provider_new.website_invalid')),
  });
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { name: '', labelKeys: [] as string[], website: '' } });

  // The browser focuses the first field itself; jsdom does not, and Escape reaches the dialog only from inside it.
  // ModalDialog (a child) has opened the dialog by now: child effects run before the parent's.
  useEffect(() => { form.setFocus('name'); }, [form]);

  const finish = (then: () => void) => {
    ref.current?.close();
    then();
  };

  // The query invalidation stays hook-level (harmless once unmounted); choosing the provider and closing the dialog go
  // through the mutate-level callbacks instead, which react-query drops once this component (the mutation's only
  // observer) has unmounted — unlike the hook-level ones, they never fire after Annuler or Escape closed the dialog.
  const create = $api.useMutation('post', PROVIDERS, {
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['get', PROVIDERS] }); },
  });

  const submit = form.handleSubmit((values) => {
    setProblem(null);
    create.mutate(
      { body: { name: values.name, website: values.website === '' ? null : values.website, pricingPage: null, labelKeys: values.labelKeys } },
      {
        onSuccess: (provider) => finish(() => onCreated({ key: provider.key, name: provider.name })),
        onError: (error) => setProblem(present(toProblem(error))),
      },
    );
  });

  const labelsError = form.formState.errors.labelKeys?.message;

  return (
    <ModalDialog title={t('provider_new.title')} onClose={() => finish(onClose)} dialogRef={ref}>
      {(titleId) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => { event.stopPropagation(); void submit(event); }}>
          {problem ? <ProblemMessage problem={problem} /> : null}
          <Field label={t('provider_new.name')} error={form.formState.errors.name?.message}>
            {(c) => <Input {...c} {...form.register('name')} />}
          </Field>
          <fieldset aria-describedby={labelsError ? labelsErrorId : undefined} className="space-y-1">
            <legend className="text-sm">{t('provider_new.labels')}</legend>
            {labels.error ? <ProblemMessage problem={present(toProblem(labels.error))} /> : null}
            {!labels.data && !labels.error ? (
              <div aria-busy="true" className="h-10 shimmer rounded-md bg-muted"><span className="sr-only">{t('provider_new.labels_loading')}</span></div>
            ) : null}
            {labels.data ? (
              <Controller control={form.control} name="labelKeys" render={({ field }) => (
                <div className="flex flex-wrap gap-x-4 gap-y-1">
                  {labels.data.map((label) => (
                    <label key={label.key} className="inline-flex items-center gap-1.5 text-sm">
                      <Checkbox checked={field.value.includes(label.key)}
                        onCheckedChange={(on) => field.onChange(on === true
                          ? [...field.value, label.key] : field.value.filter((key) => key !== label.key))} />
                      <span aria-hidden="true" className="size-2.5 rounded-full" style={{ backgroundColor: labelColour(label.color) }} />
                      {label.name}
                    </label>
                  ))}
                </div>
              )} />
            ) : null}
            {labelsError ? <span id={labelsErrorId} className="block text-sm text-destructive">{labelsError}</span> : null}
          </fieldset>
          <Field label={t('provider_new.website')} hint={t('provider_new.website_hint')} error={form.formState.errors.website?.message}>
            {(c) => <Input type="url" {...c} {...form.register('website')} />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => finish(onClose)}>{t('provider_new.cancel')}</Button>
            <Button type="submit" disabled={create.isPending || !labels.data}>{t('provider_new.create')}</Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}
