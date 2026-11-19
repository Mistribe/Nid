import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { PasswordInput } from '@/components/form/PasswordInput';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { createPublicApi, refusal, usePreSession } from './localAuth';
import { AuthFrame } from './LoginPage';
import { LOGIN_PATH } from './session';

/**
 * Issue 215 (local-accounts.md §5): the first administrator of a fresh instance. The token comes from the address the
 * server wrote in its log; the page never shows it. The setup opens no session: it ends on the sign-in page.
 */
export function SetupPage({ token, location }: { token: string | null; location: { assign: (url: string) => void } }) {
  const { t } = useTranslation();
  const { api, setCsrf } = useMemo(createPublicApi, []);
  const session = usePreSession(api, setCsrf);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const title = t('local_auth.setup.title');

  if (session === 'failed') return <AuthFrame title={title}><p role="alert">{t('local_auth.error.unreachable')}</p></AuthFrame>;
  if (session === null) return <AuthFrame title={title}><p>{t('local_auth.loading')}</p></AuthFrame>;
  if (!session.setupRequired) {
    return (
      <AuthFrame title={title}>
        <p>{t('local_auth.setup.done')}</p>
        <p><a href={LOGIN_PATH} className="underline">{t('local_auth.login.submit')}</a></p>
      </AuthFrame>
    );
  }
  if (!token) return <AuthFrame title={title}><p>{t('local_auth.setup.no_token')}</p></AuthFrame>;

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    setPending(true);
    setError(null);
    try {
      const { data, error: problem, response } = await api.fetchClient.POST('/auth/setup', {
        body: { token, name: ((fields.get('name') as string | null) ?? ''), login: ((fields.get('login') as string | null) ?? ''), password: ((fields.get('password') as string | null) ?? '') },
      });
      if (data) {
        location.assign(`${LOGIN_PATH}?login=${encodeURIComponent(data.login)}`);
        return;
      }
      setError(refusal(t, problem, response.status));
    } catch {
      setError(t('local_auth.error.unreachable'));
    } finally {
      setPending(false);
    }
  };

  return (
    <AuthFrame title={title}>
      <p>{t('local_auth.setup.intro')}</p>
      <form className="flex flex-col gap-3" onSubmit={(event) => void submit(event)}>
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        <Field label={t('local_auth.setup.name')}>{(c) => <Input {...c} name="name" autoComplete="name" required />}</Field>
        <Field label={t('local_auth.login.identifier')} hint={t('local_auth.identifier_hint')}>
          {(c) => <Input {...c} name="login" autoComplete="username" autoCapitalize="none" spellCheck={false} required />}
        </Field>
        <Field label={t('local_auth.password')} hint={t('local_auth.password_hint')}>
          {(c) => <PasswordInput {...c} name="password" autoComplete="new-password" required minLength={12} />}
        </Field>
        <Button type="submit" disabled={pending}>{t('local_auth.setup.submit')}</Button>
      </form>
    </AuthFrame>
  );
}
