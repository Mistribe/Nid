import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { refusal } from '@/app/localAuth';
import { Field } from '@/components/form/Field';
import { PasswordInput } from '@/components/form/PasswordInput';
import { InfoTip } from '@/components/InfoTip';
import { ModalDialog } from '@/components/ModalDialog';
import { Section } from '@/components/page/Section';
import { Button, buttonVariants } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import '@/locales/security';

type Dialog = { kind: 'setup'; secret: string; uri: string } | { kind: 'codes'; codes: string[]; first: boolean } | { kind: 'regenerate' } | { kind: 'disable' };

const text = (fields: FormData, name: string) => (fields.get(name) as string | null) ?? '';

/**
 * Issue 215 (local-accounts.md §10): the adult's own second factor, with local accounts. Turned on in two steps (the key
 * into the app, then its first code), which hands out ten recovery codes shown once; turned off with the password and a code.
 */
export function TwoStepSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const credential = $api.useQuery('get', '/api/v1/core/me/credential', {}, { retry: false });
  const begin = $api.useMutation('post', '/api/v1/core/me/totp');
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  if (!credential.data) return null;
  const { totpEnabled, recoveryCodesLeft, login } = credential.data;
  const close = () => { setDialog(null); setError(null); };
  const fail = (problem: unknown) => {
    const status = (problem as { status?: number }).status;
    // The key waited more than 15 minutes for its first code: it is abandoned.
    setError(status === 404 && dialog?.kind === 'setup' ? t('security.totp.expired') : refusal(t, problem, status));
  };

  const start = () => {
    setStatus(null);
    begin.mutate({}, {
      onSuccess: (data) => setDialog({ kind: 'setup', secret: data.secret, uri: data.otpauthUri }),
      onError: fail,
    });
  };

  return (
    <Section title={t('security.totp.title')} actions={totpEnabled ? (
      <>
        <Button variant="outline" onClick={() => setDialog({ kind: 'regenerate' })}>{t('security.totp.regenerate')}</Button>
        <Button variant="outline" onClick={() => setDialog({ kind: 'disable' })}>{t('security.totp.disable')}</Button>
      </>
    ) : <Button variant="outline" onClick={start} disabled={begin.isPending}>{t('security.totp.enable')}</Button>}>
      <p className="flex items-center gap-1.5 text-sm text-muted-foreground">
        {t(totpEnabled ? 'security.totp.on' : 'security.totp.off')}
        <InfoTip label={t('security.totp.title')}>{t('security.totp.about')}</InfoTip>
      </p>
      {totpEnabled ? (
        <p className={recoveryCodesLeft <= 2 ? 'text-sm text-status-warning-text' : 'text-sm text-muted-foreground'}>
          {t(recoveryCodesLeft <= 2 ? 'security.totp.codes_low' : 'security.totp.codes_left', { count: recoveryCodesLeft })}
        </p>
      ) : null}
      {status ? <p role="status" className="text-sm">{status}</p> : null}
      {!dialog && error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}

      {dialog?.kind === 'setup' ? (
        <SetupDialog secret={dialog.secret} uri={dialog.uri} error={error} onClose={close} onFail={fail}
          onCodes={(codes) => { setError(null); setDialog({ kind: 'codes', codes, first: true }); void credential.refetch(); }} />
      ) : null}
      {dialog?.kind === 'codes' ? (
        <CodesDialog codes={dialog.codes} login={login} onClose={() => {
          if (dialog.first) setStatus(t('security.totp.enabled'));
          close();
          void credential.refetch();
        }} />
      ) : null}
      {dialog?.kind === 'regenerate' ? (
        <RegenerateDialog error={error} onClose={close} onFail={fail}
          onCodes={(codes) => { setError(null); setDialog({ kind: 'codes', codes, first: false }); void credential.refetch(); }} />
      ) : null}
      {dialog?.kind === 'disable' ? (
        <DisableDialog error={error} onClose={close} onFail={fail}
          onDone={() => { close(); setStatus(t('security.totp.disabled')); void credential.refetch(); }} />
      ) : null}
    </Section>
  );
}

function Alert({ error }: { error: string | null }) {
  return error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null;
}

function SetupDialog({ secret, uri, error, onClose, onFail, onCodes }: {
  secret: string; uri: string; error: string | null; onClose: () => void; onFail: (problem: unknown) => void; onCodes: (codes: string[]) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const confirm = $api.useMutation('post', '/api/v1/core/me/totp/confirm');
  // Four by four, as the apps show it; the spaces are ignored when typed.
  const grouped = secret.match(/.{1,4}/g)?.join(' ') ?? secret;
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    confirm.mutate({ body: { code: text(new FormData(event.currentTarget), 'code') } }, {
      onSuccess: (data) => onCodes(data.codes),
      onError: onFail,
    });
  };
  return (
    <ModalDialog title={t('security.totp.setup_title')} onClose={onClose}>
      {() => (
        <div className="space-y-4">
          <p className="text-sm">{t('security.totp.setup_step_app')}</p>
          <p><a href={uri} className={buttonVariants({ variant: 'outline' })}>{t('security.totp.open_app')}</a></p>
          <div className="space-y-1">
            <p className="text-sm text-muted-foreground">{t('security.totp.key')}</p>
            <div className="flex flex-wrap items-center gap-2">
              <code className="rounded-md bg-muted px-2 py-1 font-mono text-sm break-all" data-testid="totp-secret">{grouped}</code>
              <CopyButton value={secret} />
            </div>
          </div>
          <form className="space-y-3" onSubmit={submit}>
            <p className="text-sm">{t('security.totp.setup_step_code')}</p>
            <Alert error={error} />
            <Field label={t('local_auth.code.code')}>
              {(c) => <Input {...c} name="code" inputMode="numeric" autoComplete="one-time-code" maxLength={7} required className="max-w-40" />}
            </Field>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="outline" onClick={onClose}>{t('security.totp.cancel')}</Button>
              <Button type="submit" disabled={confirm.isPending}>{t('security.totp.confirm')}</Button>
            </div>
          </form>
        </div>
      )}
    </ModalDialog>
  );
}

function CodesDialog({ codes, login, onClose }: { codes: string[]; login: string; onClose: () => void }) {
  const { t } = useTranslation();
  const download = () => {
    const content = [t('security.totp.recovery_file', { login }), '', ...codes, ''].join('\n');
    const url = URL.createObjectURL(new Blob([content], { type: 'text/plain;charset=utf-8' }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'nid-codes-de-secours.txt';
    anchor.click();
    URL.revokeObjectURL(url);
  };
  return (
    <ModalDialog title={t('security.totp.recovery_title')} onClose={onClose}>
      {() => (
        <div className="space-y-4">
          <p className="text-sm">{t('security.totp.recovery_about')}</p>
          <p className="text-sm font-medium">{t('security.totp.recovery_once')}</p>
          <ul aria-label={t('security.totp.recovery_title')} className="grid grid-cols-2 gap-x-6 gap-y-1 font-mono text-sm">
            {codes.map((code) => <li key={code}>{code}</li>)}
          </ul>
          <div className="flex flex-wrap justify-end gap-2">
            <CopyButton value={codes.join('\n')} />
            <Button type="button" variant="outline" onClick={download}>{t('security.totp.download')}</Button>
            <Button type="button" onClick={onClose}>{t('security.totp.recovery_done')}</Button>
          </div>
        </div>
      )}
    </ModalDialog>
  );
}

function RegenerateDialog({ error, onClose, onFail, onCodes }: {
  error: string | null; onClose: () => void; onFail: (problem: unknown) => void; onCodes: (codes: string[]) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const regenerate = $api.useMutation('post', '/api/v1/core/me/totp/recovery-codes');
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    regenerate.mutate({ body: { password: text(new FormData(event.currentTarget), 'password') } }, {
      onSuccess: (data) => onCodes(data.codes),
      onError: onFail,
    });
  };
  return (
    <ModalDialog title={t('security.totp.regenerate_title')} onClose={onClose}>
      {() => (
        <form className="space-y-3" onSubmit={submit}>
          <p className="text-sm">{t('security.totp.regenerate_intro')}</p>
          <Alert error={error} />
          <Field label={t('local_auth.password')}>{(c) => <PasswordInput {...c} name="password" autoComplete="current-password" required />}</Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t('security.totp.cancel')}</Button>
            <Button type="submit" disabled={regenerate.isPending}>{t('security.totp.regenerate')}</Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}

function DisableDialog({ error, onClose, onFail, onDone }: {
  error: string | null; onClose: () => void; onFail: (problem: unknown) => void; onDone: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const disable = $api.useMutation('delete', '/api/v1/core/me/totp');
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const fields = new FormData(event.currentTarget);
    disable.mutate({ body: { password: text(fields, 'password'), code: text(fields, 'code') } }, { onSuccess: onDone, onError: onFail });
  };
  return (
    <ModalDialog title={t('security.totp.disable_title')} onClose={onClose}>
      {() => (
        <form className="space-y-3" onSubmit={submit}>
          <p className="text-sm">{t('security.totp.disable_intro')}</p>
          <Alert error={error} />
          <Field label={t('local_auth.password')}>{(c) => <PasswordInput {...c} name="password" autoComplete="current-password" required />}</Field>
          <Field label={t('security.totp.code')}>
            {(c) => <Input {...c} name="code" autoComplete="one-time-code" autoCapitalize="characters" spellCheck={false} required />}
          </Field>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={onClose}>{t('security.totp.cancel')}</Button>
            <Button type="submit" variant="destructive" disabled={disable.isPending}>{t('security.totp.disable')}</Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}

function CopyButton({ value }: { value: string }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  return (
    <Button type="button" variant="outline" size="sm" onClick={() => {
      void navigator.clipboard?.writeText(value).then(() => setCopied(true), () => setCopied(false));
    }}>
      {t(copied ? 'security.totp.copied' : 'security.totp.copy')}
    </Button>
  );
}
