import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { PasswordInput } from '@/components/form/PasswordInput';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { createPublicApi, refusal, usePreSession } from './localAuth';
import { AuthFrame } from './LoginPage';
import { LOGIN_PATH } from './session';

type Link = { purpose: string; name: string; login?: string | null };

/**
 * Issue 215 (local-accounts.md §7, §8): a one-time link an administrator gave. `enroll`: the adult chooses their
 * identifier and password; `reset`: a new password, and their other sessions close. Neither opens a session.
 */
export function PasswordLinkPage({ token, location }: { token: string | null; location: { assign: (url: string) => void } }) {
  const { t } = useTranslation();
  const { api, setCsrf } = useMemo(createPublicApi, []);
  const session = usePreSession(api, setCsrf);
  const [link, setLink] = useState<Link | null | 'unusable'>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  const ready = session !== null && session !== 'failed';
  useEffect(() => {
    if (!ready) return;
    if (!token) { setLink('unusable'); return; }
    let live = true;
    api.fetchClient.POST('/auth/link/inspect', { body: { token } }).then(({ data }) => {
      if (live) setLink(data ?? 'unusable');
    }, () => { if (live) setLink('unusable'); });
    return () => { live = false; };
  }, [api, ready, token]);

  const title = link !== null && link !== 'unusable' && link.purpose === 'reset' ? t('local_auth.link.reset_title') : t('local_auth.link.enroll_title');
  if (session === 'failed') return <AuthFrame title={title}><p role="alert">{t('local_auth.error.unreachable')}</p></AuthFrame>;
  if (session === null || link === null) return <AuthFrame title={title}><p>{t('local_auth.loading')}</p></AuthFrame>;
  if (link === 'unusable') return <AuthFrame title={title}><p role="alert">{t('local_auth.error.auth_link_unusable')}</p></AuthFrame>;

  const enroll = link.purpose === 'enroll';
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    setPending(true);
    setError(null);
    try {
      const { data, error: problem, response } = await api.fetchClient.POST('/auth/link/complete', {
        body: { token, login: enroll ? ((fields.get('login') as string | null) ?? '') : null, password: ((fields.get('password') as string | null) ?? '') },
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
      <p>{t(enroll ? 'local_auth.link.enroll_intro' : 'local_auth.link.reset_intro', { name: link.name })}</p>
      <form className="flex flex-col gap-3" onSubmit={(event) => void submit(event)}>
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {enroll ? (
          <Field label={t('local_auth.login.identifier')} hint={t('local_auth.identifier_hint')}>
            {(c) => <Input {...c} name="login" autoComplete="username" autoCapitalize="none" spellCheck={false} required defaultValue={link.login ?? ''} />}
          </Field>
        ) : (
          // The identifier, for the password manager to save the pair: shown, not editable.
          <Field label={t('local_auth.login.identifier')}>
            {(c) => <Input {...c} name="login" autoComplete="username" readOnly value={link.login ?? ''} />}
          </Field>
        )}
        <Field label={t('local_auth.new_password')} hint={t('local_auth.password_hint')}>
          {(c) => <PasswordInput {...c} name="password" autoComplete="new-password" required minLength={12} />}
        </Field>
        <Button type="submit" disabled={pending}>{t(enroll ? 'local_auth.link.enroll_submit' : 'local_auth.link.reset_submit')}</Button>
      </form>
    </AuthFrame>
  );
}
