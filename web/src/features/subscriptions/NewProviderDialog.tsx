import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { LABELS, PROVIDERS } from './api';
import { Field } from './edits/Field';

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
 * 1E2b2 design §2.2: a custom provider, created right away and handed back to the picker. A native modal dialog in a
 * portal: the wizard is a form, and a form never nests another. React still bubbles `submit` through the portal, so the
 * dialog's form stops it. Closing goes through `finish`, which closes the dialog before the picker moves the focus:
 * the browser's own focus restoration would otherwise run last.
 */
export function NewProviderDialog({ initialName, onCreated, onClose }: {
  initialName: string; onCreated: (provider: { key: string; name: string }) => void; onClose: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const labelsErrorId = useId();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const labels = $api.useQuery('get', LABELS);

  const schema = z.object({
    name: z.string().trim().min(1, t('provider_new.name_required')).max(MAX_NAME, t('provider_new.name_too_long', { max: MAX_NAME })),
    labelKeys: z.array(z.string()).min(1, t('provider_new.labels_required')),
    website: z.string().trim().max(MAX_URL, t('provider_new.website_too_long', { max: MAX_URL }))
      .refine((value) => value === '' || isHttpUrl(value), t('provider_new.website_invalid')),
  });
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { name: initialName, labelKeys: [] as string[], website: '' } });

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    // The browser focuses the first field itself; jsdom does not, and Escape reaches the dialog only from inside it.
    form.setFocus('name');
    return () => { if (dialog?.open) dialog.close(); };
  }, [form]);

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

  return createPortal(
    <dialog ref={ref} aria-labelledby={titleId}
      onCancel={(event) => { event.preventDefault(); finish(onClose); }}
      onKeyDown={(event) => { if (event.key === 'Escape') { event.preventDefault(); finish(onClose); } }}
      className="m-auto w-full max-w-md rounded-md border bg-background p-4 text-foreground backdrop:bg-black/50">
      <form noValidate className="space-y-3" onSubmit={(event) => { event.stopPropagation(); void submit(event); }}>
        <h2 id={titleId} className="text-lg font-semibold">{t('provider_new.title')}</h2>
        {problem ? <ProblemMessage problem={problem} /> : null}
        <Field label={t('provider_new.name')} error={form.formState.errors.name?.message}>
          {(c) => <input {...c} {...form.register('name')} className="w-full rounded-md border px-3 py-2" />}
        </Field>
        <fieldset aria-describedby={labelsError ? labelsErrorId : undefined} className="space-y-1">
          <legend className="text-sm">{t('provider_new.labels')}</legend>
          {labels.error ? <ProblemMessage problem={present(toProblem(labels.error))} /> : null}
          {!labels.data && !labels.error ? (
            <div aria-busy="true" className="h-10 animate-pulse rounded-md bg-muted"><span className="sr-only">{t('provider_new.labels_loading')}</span></div>
          ) : null}
          {labels.data ? (
            <Controller control={form.control} name="labelKeys" render={({ field }) => (
              <div className="flex flex-wrap gap-x-4 gap-y-1">
                {labels.data.map((label) => (
                  <label key={label.key} className="inline-flex items-center gap-1.5 text-sm">
                    <input type="checkbox" checked={field.value.includes(label.key)}
                      onChange={(event) => field.onChange(event.target.checked
                        ? [...field.value, label.key] : field.value.filter((key) => key !== label.key))} />
                    <span aria-hidden="true" className="size-2.5 rounded-full" style={{ backgroundColor: label.color }} />
                    {label.name}
                  </label>
                ))}
              </div>
            )} />
          ) : null}
          {labelsError ? <span id={labelsErrorId} className="block text-sm text-destructive">{labelsError}</span> : null}
        </fieldset>
        <Field label={t('provider_new.website')} hint={t('provider_new.website_hint')} error={form.formState.errors.website?.message}>
          {(c) => <input type="url" {...c} {...form.register('website')} className="w-full rounded-md border px-3 py-2" />}
        </Field>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={() => finish(onClose)}>{t('provider_new.cancel')}</Button>
          <Button type="submit" disabled={create.isPending || !labels.data}>{t('provider_new.create')}</Button>
        </div>
      </form>
    </dialog>,
    document.body,
  );
}
