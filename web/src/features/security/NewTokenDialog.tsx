import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Field } from '../subscriptions/edits/Field';
import { EXPIRIES, PRESETS, TOKENS, type Preset } from './tokens';

const input = 'rounded-md border px-3 py-2';

/**
 * Spec 4A §8: name, rights (« Lecture + propositions » by default) and validity, then the secret shown once. The secret
 * lives in this component's state only: it is gone when the dialog closes, and never reaches the query cache.
 */
export function NewTokenDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [secret, setSecret] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const schema = z.object({
    name: z.string().trim().min(1, t('edit.required')).max(80),
    preset: z.enum(['read', 'propose', 'direct']),
    expiresInDays: z.coerce.number(),
  });
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { name: '', preset: 'propose' as Preset, expiresInDays: 90 } });
  const create = $api.useMutation('post', TOKENS, {
    onSuccess: (created) => {
      setSecret(created.secret);
      void queryClient.invalidateQueries({ queryKey: ['get', TOKENS] });
    },
  });
  const preset = form.watch('preset');
  const problem = create.error ? toProblem(create.error) : null;

  if (secret) {
    return (
      <ModalDialog title={t('security.tokens.created_title')} onClose={onClose}>
        {() => (
          <div className="space-y-3">
            <p>{t('security.tokens.created_once')}</p>
            <code className="block break-all rounded-md border bg-muted p-2 font-mono text-sm">{secret}</code>
            <p role="status">{copied ? t('security.tokens.copied') : ''}</p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={() => { void navigator.clipboard.writeText(secret).then(() => setCopied(true)); }}>
                {t('security.tokens.copy')}
              </Button>
              <Button type="button" onClick={onClose}>{t('security.tokens.done')}</Button>
            </div>
          </div>
        )}
      </ModalDialog>
    );
  }

  const submit = form.handleSubmit((values) => create.mutate({ body: values }));

  return (
    <ModalDialog title={t('security.tokens.create')} onClose={onClose}>
      {(titleId) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
          {problem?.code === 'core.token_limit_reached'
            ? <p role="alert">{t('security.tokens.limit_reached')}</p>
            : problem ? <ProblemMessage problem={present(problem)} /> : null}
          <Field label={t('security.tokens.name')} error={form.formState.errors.name?.message}>
            {(c) => <input {...c} {...form.register('name')} className={input} />}
          </Field>
          <fieldset className="space-y-2">
            <legend className="font-medium">{t('security.tokens.preset_label')}</legend>
            {PRESETS.map((p) => (
              <label key={p} className="flex items-start gap-2">
                <input type="radio" value={p} {...form.register('preset')} className="mt-1" />
                <span>
                  <span className="font-medium">{t(`security.tokens.preset.${p}`)}</span>
                  <span className="block text-sm text-muted-foreground">{t(`security.tokens.preset_hint.${p}`)}</span>
                </span>
              </label>
            ))}
          </fieldset>
          {preset === 'direct' ? <p role="note" className="rounded-md border p-2">{t('security.tokens.direct_warning')}</p> : null}
          <fieldset className="space-y-2">
            <legend className="font-medium">{t('security.tokens.expiry_label')}</legend>
            {EXPIRIES.map((days) => (
              <label key={days} className="mr-4 inline-flex items-center gap-2">
                <input type="radio" value={days} {...form.register('expiresInDays')} />
                {t(`security.tokens.expiry.${days}`)}
              </label>
            ))}
          </fieldset>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t('security.tokens.cancel')}</Button>
            <Button type="submit" disabled={create.isPending}>{t('security.tokens.submit')}</Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}
