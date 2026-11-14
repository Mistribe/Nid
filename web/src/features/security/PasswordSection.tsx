import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { refusal } from '@/app/localAuth';
import { useSession } from '@/app/Providers';
import { Field } from '@/components/form/Field';
import { PasswordInput } from '@/components/form/PasswordInput';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { formatInstant } from '@/lib/format';
import '@/locales/security';

/**
 * Issue 215 (local-accounts.md §8): the adult's own password, with local accounts. The current one is asked; the
 * other sessions close and this one is reissued, so the page reloads to take its new CSRF token.
 */
export function PasswordSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { reload } = useSession();
  const credential = $api.useQuery('get', '/api/v1/core/me/credential', {}, { retry: false });
  const change = $api.useMutation('put', '/api/v1/core/me/password');
  const [error, setError] = useState<string | null>(null);

  // An adult who signs in through the identity provider has no local account: nothing to show.
  if (!credential.data) return null;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    setError(null);
    change.mutate({ body: { current: ((fields.get('current') as string | null) ?? ''), new: ((fields.get('new') as string | null) ?? '') } }, {
      onSuccess: () => reload(),
      onError: (problem) => setError(refusal(t, problem, (problem as { status?: number }).status)),
    });
  };

  return (
    <Section title={t('security.password_title')}>
      <p className="text-sm text-muted-foreground">
        {t('security.password_state', { login: credential.data.login, at: formatInstant(credential.data.passwordChangedAt) })}
      </p>
      <form className="max-w-sm space-y-3" onSubmit={submit}>
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {/* The identifier, for the password manager to update the right entry. */}
        <input type="text" name="username" autoComplete="username" value={credential.data.login} readOnly hidden />
        <Field label={t('security.password_current')}>{(c) => <PasswordInput {...c} name="current" autoComplete="current-password" required />}</Field>
        <Field label={t('local_auth.new_password')} hint={t('local_auth.password_hint')}>
          {(c) => <PasswordInput {...c} name="new" autoComplete="new-password" required minLength={12} />}
        </Field>
        <p className="text-sm text-muted-foreground">{t('security.password_others_close')}</p>
        <Button type="submit" disabled={change.isPending}>{t('security.password_submit')}</Button>
      </form>
    </Section>
  );
}
