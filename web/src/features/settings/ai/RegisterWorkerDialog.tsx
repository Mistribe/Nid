import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, fieldError, WORKER_PAIR, WORKERS, type Worker } from '@/api/ai';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { Folded } from '@/components/form/Folded';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import '@/locales/ai';

const CONSOLE = 'docker compose exec worker dotnet Nid.Ai.Worker.dll pairing-code';

/** The address's machine name (`worker` for https://worker:8443): a worker's name when the admin gives none. */
function hostOf(url: string): string {
  try {
    return new URL(url.trim()).hostname;
  } catch {
    return url.trim();
  }
}

/**
 * « Ajouter un worker » in two steps (#476): its address (name, placement and proxy folded under « Personnaliser »), then the
 * fingerprint to compare with the worker's console and its pairing code. A worker left at the second step (`pending`) resumes there;
 * one whose key Nid never read (the worker was down) starts again at its address, which registers the same row anew.
 */
export function RegisterWorkerDialog({ pending, onClose, onDone }: { pending?: Worker; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['get', WORKERS] });
  const [url, setUrl] = useState(pending?.url ?? 'https://worker:8443');
  const [name, setName] = useState(pending?.name ?? '');
  const [placement, setPlacement] = useState(pending?.placement ?? 'local');
  const [proxy, setProxy] = useState(pending?.proxyDeclared ?? false);
  const [code, setCode] = useState('');
  const [registered, setRegistered] = useState(pending?.keyFingerprint ? { id: pending.id, fingerprint: pending.keyFingerprint } : null);
  const register = $api.useMutation('post', WORKERS, {
    onSuccess: (worker) => { setRegistered(worker); void refresh(); },
  });
  const pair = $api.useMutation('post', WORKER_PAIR);
  const failure = register.error ?? pair.error;
  const urlError = fieldError(t, register.error, 'url', t('ai.register.url_required'));
  const nameError = fieldError(t, register.error, 'name', t('ai.workers.name_required'));

  return (
    <FormDialog title={t('ai.register.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => registered ? (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          pair.mutate({ params: { path: { id: registered.id } }, body: { fingerprint: registered.fingerprint, code } }, {
            onSuccess: (worker) => { void refresh(); done(t('ai.register.done', { name: worker.name })); },
          });
        }}>
          <p className="text-sm text-muted-foreground">{t('ai.register.step_pair')}</p>
          {failure ? <ProblemMessage problem={aiProblem(t, failure)} /> : null}
          <p className="text-sm">{t('ai.register.console')}</p>
          <pre className="rounded-md bg-muted px-3 py-2 text-xs break-all whitespace-pre-wrap">{CONSOLE}</pre>
          <div className="space-y-1">
            <div className="flex items-center gap-1.5 text-sm">
              {t('ai.register.fingerprint')}
              <InfoTip label={t('ai.register.fingerprint')}>{t('ai.register.fingerprint_tip')}</InfoTip>
            </div>
            <p className="font-mono text-sm break-all" data-testid="worker-fingerprint">{registered.fingerprint}</p>
            <p className="text-sm text-muted-foreground">{t('ai.register.mismatch')}</p>
          </div>
          <Field label={t('ai.register.code')} hint={t('ai.register.code_hint')}>
            {(c) => <Input {...c} value={code} onChange={(e) => setCode(e.target.value)} autoComplete="off" autoCapitalize="characters" spellCheck={false} />}
          </Field>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={pair.isPending}>{t('ai.register.submit')}</Button>
          </div>
        </form>
      ) : (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          register.mutate({ body: { url: url.trim(), name: name.trim() || hostOf(url), placement, proxyDeclared: proxy } });
        }}>
          <p className="text-sm text-muted-foreground">{t('ai.register.step_address')}</p>
          {failure && !urlError && !nameError ? <ProblemMessage problem={aiProblem(t, failure)} /> : null}
          <Field label={t('ai.register.url')} hint={t('ai.register.url_hint')} error={urlError}>
            {(c) => <Input {...c} type="url" value={url} onChange={(e) => setUrl(e.target.value)} autoComplete="off" spellCheck={false} />}
          </Field>
          <Field label={t('ai.register.name')} hint={t('ai.register.name_hint')} error={nameError}>
            {(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} maxLength={60} />}
          </Field>
          <Folded label={t('ai.register.customize')} hasError={false}>
            <p className="text-sm text-muted-foreground">{t('ai.register.customize_hint')}</p>
            <Field label={t('ai.register.placement')} tip={<InfoTip label={t('ai.register.placement')}>{t('ai.data.remote_tip')}</InfoTip>}>
              {(c) => (
                <SelectField {...c} value={placement} onValueChange={setPlacement} options={[
                  { value: 'local', label: t('ai.register.placement_local') },
                  { value: 'remote', label: t('ai.register.placement_remote') },
                ]} />
              )}
            </Field>
            <SwitchField label={t('ai.register.proxy')} checked={proxy} onCheckedChange={setProxy}
              tip={<InfoTip label={t('ai.register.proxy')}>{t('ai.register.proxy_tip')}</InfoTip>} />
          </Folded>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={register.isPending}>{t('ai.register.next')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
