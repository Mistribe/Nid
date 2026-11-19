import { useMemo, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { PasswordInput } from '@/components/form/PasswordInput';
import { Wordmark } from '@/components/Logo';
import { Button, buttonVariants } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { codeOf, createPublicApi, refusal, usePreSession } from './localAuth';
import { oidcLoginUrl, safeReturnUrl } from './session';

/** The public pages' frame: one content window on the halo, as the signed-out page. */
export function AuthFrame({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center p-4">
      <div className="content-pane rounded-pane flex flex-col gap-4 p-6 shadow-card">
        <Wordmark className="h-8" />
        <h1 className="text-2xl font-semibold">{title}</h1>
        {children}
      </div>
    </main>
  );
}

/**
 * Issue 215 (local-accounts.md §9, §11, §14.5): sign in with an identifier and a password; with both ways on, the
 * identity provider is one link away. One message for every refusal: it never says whether the identifier exists.
 */
export function LoginPage({ returnUrl, login, location }: {
  returnUrl: string | null; login: string | null; location: { assign: (url: string) => void };
}) {
  const { t } = useTranslation();
  const { api, setCsrf } = useMemo(createPublicApi, []);
  const session = usePreSession(api, setCsrf);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  // Issue 215, local-accounts.md §10: after a right password, a code when the adult turned the second factor on.
  const [step, setStep] = useState<'password' | 'code' | 'recovery'>('password');
  const target = safeReturnUrl(returnUrl);

  if (session === 'failed') return <AuthFrame title={t('local_auth.login.title')}><p role="alert">{t('local_auth.error.unreachable')}</p></AuthFrame>;
  if (session === null) return <AuthFrame title={t('local_auth.login.title')}><p>{t('local_auth.loading')}</p></AuthFrame>;
  if (session.authenticated) {
    location.assign(target);
    return null;
  }

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    setPending(true);
    setError(null);
    try {
      const { data, error: problem, response } = await api.fetchClient.POST('/auth/login/password', {
        body: { login: ((fields.get('login') as string | null) ?? ''), password: ((fields.get('password') as string | null) ?? ''), returnUrl: target },
      });
      if (data?.next === 'totp') {
        setStep('code');
        return;
      }
      if (data) {
        location.assign(safeReturnUrl(data.returnUrl ?? target));
        return;
      }
      setError(refusal(t, problem, response.status));
    } catch {
      setError(t('local_auth.error.unreachable'));
    } finally {
      setPending(false);
    }
  };

  const submitCode = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const typed = (new FormData(event.currentTarget).get('code') as string | null) ?? '';
    setPending(true);
    setError(null);
    try {
      const { data, error: problem, response } = await api.fetchClient.POST('/auth/login/totp', {
        body: step === 'recovery' ? { recoveryCode: typed } : { code: typed },
      });
      if (data) {
        location.assign(safeReturnUrl(data.returnUrl ?? target));
        return;
      }
      // Too slow, too many wrong codes, or locked: the password again.
      if (codeOf(problem) === 'auth.mfa_expired') setStep('password');
      setError(refusal(t, problem, response.status));
    } catch {
      setError(t('local_auth.error.unreachable'));
    } finally {
      setPending(false);
    }
  };

  if (step !== 'password') {
    const recovery = step === 'recovery';
    return (
      <AuthFrame title={t('local_auth.code.title')}>
        <p className="text-sm text-muted-foreground">{t(recovery ? 'local_auth.code.recovery_intro' : 'local_auth.code.intro')}</p>
        <form key={step} className="flex flex-col gap-3" onSubmit={(event) => void submitCode(event)}>
          {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
          <Field label={t(recovery ? 'local_auth.code.recovery' : 'local_auth.code.code')}>
            {(c) => recovery
              ? <Input {...c} name="code" autoComplete="off" autoCapitalize="characters" spellCheck={false} required autoFocus />
              : <Input {...c} name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]{6,7}" maxLength={7} required autoFocus />}
          </Field>
          <Button type="submit" disabled={pending}>{t('local_auth.code.submit')}</Button>
        </form>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="link" className="px-0" onClick={() => { setError(null); setStep(recovery ? 'code' : 'recovery'); }}>
            {t(recovery ? 'local_auth.code.use_app' : 'local_auth.code.use_recovery')}
          </Button>
          <Button type="button" variant="link" className="px-0" onClick={() => { setError(null); setStep('password'); }}>
            {t('local_auth.code.back')}
          </Button>
        </div>
      </AuthFrame>
    );
  }

  const oidc = session.methods.includes('oidc');
  return (
    <AuthFrame title={t('local_auth.login.title')}>
      {session.setupRequired ? <p role="status" className="rounded-md border border-status-warning/60 bg-status-warning/10 p-3 text-sm">{t('local_auth.login.setup_required')}</p> : null}
      <form className="flex flex-col gap-3" onSubmit={(event) => void submit(event)}>
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        <Field label={t('local_auth.login.identifier')}>
          {(c) => <Input {...c} name="login" autoComplete="username" autoCapitalize="none" spellCheck={false} required defaultValue={login ?? ''} />}
        </Field>
        <Field label={t('local_auth.password')}>{(c) => <PasswordInput {...c} name="password" autoComplete="current-password" required />}</Field>
        <Button type="submit" disabled={pending}>{t('local_auth.login.submit')}</Button>
      </form>
      <p className="text-sm text-muted-foreground">{t('local_auth.login.forgotten')}</p>
      {oidc ? (
        <p><a href={oidcLoginUrl(target)} className={buttonVariants({ variant: 'outline' })}>{t('local_auth.login.with_identity_provider')}</a></p>
      ) : null}
    </AuthFrame>
  );
}
