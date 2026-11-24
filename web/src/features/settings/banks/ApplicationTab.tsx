import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { Copy } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import type { BanksSearch } from '@/app/router';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { StepForm } from '@/components/page/StepForm';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { ConnectBankDialog } from './ConnectBankDialog';
import { ConnectionTestResult } from './ConnectionTestResult';
import { APPLICATION, BANKING_STATUS, PORTAL_URL, PRIVACY_URL, TERMS_URL, bankErrorText, presentBankError, useBankingStatus } from '@/api/banking';
import { formatInstantDate } from '@/lib/format';
import '@/locales/banks';
import { useCopy } from '@/lib/clipboard';

const TEST = '/api/v1/banking/application/test' as const;

type Test = components['schemas']['BankingConnectionTestView'];
type MyApplication = components['schemas']['MyApplicationView'];
const MAX_KEY_BYTES = 16 * 1024;

/** The wizard's steps (#414), in order; the address keeps the one reached (`?step=`, from 1). */
const STEPS = ['need', 'account', 'application', 'details', 'portal', 'declare', 'test', 'bank'] as const;
type StepId = (typeof STEPS)[number];

/** The step that fixes what a test found, if one does: a refused key, an application not active yet, a redirect URL missing. */
export function fixStep(test: Test): StepId | null {
  if (test.outcome === 'rejected') return 'declare';
  if (test.outcome !== 'accepted') return null;
  if (test.redirectUrlAllowed === false) return 'details';
  return test.active === false ? 'portal' : null;
}

/**
 * Réglages › Banques › Mon application (#382): the Enable Banking application this adult reads their banks with. Without one, a wizard
 * leads them through the portal (#414); with one, its summary. The private key is write-only: sent once, never read back, never shown.
 */
export default function ApplicationTab({ focus = false }: { focus?: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const search: BanksSearch = useSearch({ strict: false });
  const mine = $api.useQuery('get', APPLICATION);
  const [replacing, setReplacing] = useState(false);
  const [asking, setAsking] = useState(false);
  const [tested, setTested] = useState<Test | null>(null);
  const menu = useRef<HTMLButtonElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const changed = () => {
    void queryClient.invalidateQueries({ queryKey: ['get', APPLICATION] });
    void queryClient.invalidateQueries({ queryKey: ['get', BANKING_STATUS] });
  };
  const remove = $api.useMutation('delete', APPLICATION, {
    onSuccess: () => { toast.success(t('banks.application.removed')); setTested(null); changed(); },
    onError: (error) => { toast.error(presentBankError(t, error).detail ?? t('banks.application.failed')); },
  });
  const test = $api.useMutation('post', TEST, {
    onMutate: () => setTested(null),
    onSuccess: setTested,
    onError: (error) => { toast.error(presentBankError(t, error).detail ?? t('banks.application.failed')); },
  });

  // `?tab=application`: once the section shows, focusing its heading scrolls the page to it.
  useEffect(() => { if (focus && mine.isSuccess) heading.current?.focus(); }, [focus, mine.isSuccess]);

  if (mine.error) return <ProblemMessage problem={present(toProblem(mine.error))} />;
  if (!mine.isSuccess) return <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.application.loading')}</span></Skeleton>;
  const view = mine.data;
  const done = (message: string) => { toast.success(message); setTested(null); setReplacing(false); changed(); };
  const finish = () => {
    toast.success(t('banks.wizard.finished'));
    changed();
    void navigate({ to: '/settings/banks', search: { tab: 'application' }, replace: true });
  };

  return (
    <Section id="application" title={t('banks.application.title')} headingRef={heading}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('banks.application.intro')}</p>
      {view && search.step === undefined ? (
        <div className="space-y-3">
          <div className="flex flex-wrap items-start justify-between gap-2">
            <StatLine items={[
              { label: t('banks.application.id'), value: view.applicationId },
              { label: t('banks.application.key'), value: view.keyStatus === 'loaded'
                ? t('banks.application.key_loaded', { date: formatInstantDate(view.keyLoadedAt) })
                : <span className="text-status-warning-text">{t('banks.application.key_unreadable')}</span> },
            ]} />
            <span className="flex items-center gap-2">
              <Button type="button" variant="outline" disabled={test.isPending} onClick={() => test.mutate({})}>{t('banks.application.test')}</Button>
              <RowMenu id="my-application" label={t('banks.application.actions')} triggerRef={menu} items={[
                { label: t('banks.application.replace'), onSelect: () => setReplacing(true) },
                { label: t('banks.application.remove'), onSelect: () => setAsking(true), destructive: true },
              ]} />
            </span>
          </div>
          <div aria-live="polite" className="space-y-1">{tested ? <ConnectionTestResult result={tested} /> : null}</div>
        </div>
      ) : <Wizard current={view ?? null} step={(search.step ?? 1) - 1} onFinish={finish} />}
      {replacing && view ? <ApplicationDialog current={view} onClose={() => setReplacing(false)} onDone={done} /> : null}
      {asking ? (
        <ConfirmFromMenu label={t('banks.application.remove')} confirmation={t('banks.application.remove_confirm')} pending={remove.isPending} returnFocus={menu}
          onClose={() => setAsking(false)} onConfirm={() => remove.mutate({})} />
      ) : null}
    </Section>
  );
}

/**
 * #414: setting up one's own Enable Banking application, one step per screen, the step kept in the address so the adult comes back to it
 * from the portal. « Continuer » on « Déclarer » saves the application; a refusal stays on that step.
 */
function Wizard({ current, step, onFinish }: { current: MyApplication | null; step: number; onFinish: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const redirectUrl = useBankingStatus().data?.redirectUrl ?? '';
  const [applicationId, setApplicationId] = useState(current?.applicationId ?? '');
  const [privateKey, setPrivateKey] = useState('');
  const [tested, setTested] = useState<Test | null>(null);
  const [connecting, setConnecting] = useState(false);
  const save = $api.useMutation('put', APPLICATION);
  const test = $api.useMutation('post', TEST, { onMutate: () => setTested(null), onSuccess: setTested });
  const toStep = (index: number) => void navigate({ to: '/settings/banks', search: { tab: 'application', step: index + 1 }, replace: true });
  const title = (id: StepId) => t(`banks.wizard.${id}.title`);
  const fix = tested ? fixStep(tested) : null;
  const restricted = <InfoTip label={t('banks.wizard.restricted')}>{t('banks.wizard.restricted_tip')}</InfoTip>;

  const content: Record<StepId, ReactNode> = {
    need: (
      <>
        <p>{t('banks.wizard.need.what')} <InfoTip label={t('banks.wizard.enable_banking')}>{t('banks.wizard.enable_banking_tip')}</InfoTip></p>
        <p>{t('banks.wizard.need.reads')} <InfoTip label={t('banks.wizard.psd2')}>{t('banks.wizard.psd2_tip')}</InfoTip></p>
        <p>{t('banks.wizard.need.free')} {restricted}</p>
        <p>{t('banks.wizard.need.time')}</p>
      </>
    ),
    account: (
      <>
        <p>{t('banks.wizard.account.body')}</p>
        <p><a href={PORTAL_URL} target="_blank" rel="noreferrer" className="underline underline-offset-4">{t('banks.wizard.open_portal')}</a></p>
      </>
    ),
    application: (
      <>
        <p>{t('banks.wizard.application.body')}</p>
        <ol className="list-decimal space-y-2 pl-5">
          <li>{t('banks.wizard.application.environment')} <InfoTip label={t('banks.wizard.production')}>{t('banks.wizard.production_tip')}</InfoTip></li>
          <li>{t('banks.wizard.application.key')} <InfoTip label={t('banks.application.private_key')}>{t('banks.wizard.key_tip')}</InfoTip></li>
        </ol>
      </>
    ),
    details: (
      <>
        <p>{t('banks.wizard.details.body')}</p>
        <div className="space-y-4 rounded-md border p-4">
          <CopyField label={t('banks.wizard.details.name')} value={t('banks.wizard.details.name_value')}
            hint={t('banks.wizard.details.name_hint')} mono={false} />
          <CopyField label={t('banks.wizard.details.redirect')} value={redirectUrl}
            tip={<InfoTip label={t('banks.wizard.details.redirect')}>{t('banks.wizard.details.redirect_tip')}</InfoTip>} />
          <CopyField label={t('banks.wizard.details.description')} value={t('banks.wizard.details.description_value')}
            hint={t('banks.wizard.details.description_hint')} mono={false} />
          <div className="space-y-1">
            <p className="font-medium">{t('banks.wizard.details.email')}</p>
            <p className="text-muted-foreground">{t('banks.wizard.details.email_hint')}</p>
          </div>
          <CopyField label={t('banks.wizard.details.privacy')} value={PRIVACY_URL} />
          <CopyField label={t('banks.wizard.details.terms')} value={TERMS_URL} hint={t('banks.wizard.details.urls_hint')} />
        </div>
        <p>{t('banks.wizard.details.register')}</p>
      </>
    ),
    portal: (
      <>
        <p>{t('banks.wizard.portal.body')} {restricted}</p>
        <p>{t('banks.wizard.portal.own')}</p>
      </>
    ),
    declare: (
      <>
        {save.error && fieldErrorsOf(save.error).length === 0 ? <ProblemMessage problem={presentBankError(t, save.error)} /> : null}
        <ApplicationFields error={save.error} replacing={current !== null} applicationId={applicationId} setApplicationId={setApplicationId}
          privateKey={privateKey} setPrivateKey={setPrivateKey} />
      </>
    ),
    test: (
      <>
        <p>{t('banks.wizard.test.body')}</p>
        <div><Button type="button" variant="outline" disabled={test.isPending} onClick={() => test.mutate({})}>{t('banks.application.test')}</Button></div>
        <div aria-live="polite" className="space-y-1">
          {test.error ? <ProblemMessage problem={presentBankError(t, test.error)} /> : null}
          {tested ? <ConnectionTestResult result={tested} redirectUrl={redirectUrl || undefined} /> : null}
          {tested?.outcome === 'accepted' && !fix ? <p>{t('banks.wizard.test.ready')}</p> : null}
        </div>
        {fix ? (
          <div><Button type="button" variant="outline" onClick={() => toStep(STEPS.indexOf(fix))}>
            {t('banks.wizard.test.fix', { n: STEPS.indexOf(fix) + 1, step: title(fix) })}
          </Button></div>
        ) : null}
      </>
    ),
    bank: (
      <>
        <p>{t('banks.wizard.bank.body')} <InfoTip label={t('banks.wizard.consent')}>{t('banks.wizard.consent_tip')}</InfoTip></p>
        <div><Button type="button" onClick={() => setConnecting(true)}>{t('banks.connect')}</Button></div>
        {connecting ? <ConnectBankDialog onClose={() => setConnecting(false)} /> : null}
      </>
    ),
  };

  return (
    <div className="max-w-prose text-sm">
      <StepForm step={step} onStep={toStep} submitLabel={t('banks.wizard.finish')} pending={save.isPending} onSubmit={onFinish}
        steps={STEPS.map((id) => ({ id, title: title(id), fields: id === 'declare' ? ['declare'] : [], content: <div className="space-y-3">{content[id]}</div> }))}
        trigger={async (fields) => {
          if (!fields.includes('declare')) return true;
          const key = privateKey.trim();
          try {
            await save.mutateAsync({ body: { applicationId: applicationId.trim(), ...(key ? { privateKey: key } : {}) } });
            setPrivateKey('');
            return true;
          } catch {
            return false;
          }
        }} />
    </div>
  );
}

/** A value the adult pastes into the portal, exactly: read-only, selected on focus, with a copy button. */
function CopyField({ label, value, tip, hint, mono = true }: { label: string; value: string; tip?: ReactNode; hint?: string; mono?: boolean }) {
  const { t } = useTranslation();
  const copy = useCopy();
  return (
    <div className="flex flex-wrap items-end gap-2">
      <div className="min-w-0 flex-1 basis-64">
        <Field label={label} tip={tip} hint={hint}>
          {(c) => <Input {...c} readOnly value={value} className={mono ? 'font-mono text-sm' : undefined} onFocus={(e) => e.currentTarget.select()} />}
        </Field>
      </div>
      <Button type="button" variant="outline" aria-label={t('banks.wizard.copy_label', { label })} onClick={() => copy(value, () => toast.success(t('banks.wizard.copied')))}>
        <Copy aria-hidden="true" />{t('banks.wizard.copy')}
      </Button>
    </div>
  );
}

/** The identifier and the key; with an application already declared, an empty key keeps the one loaded. The key lives in the caller's state only. */
function ApplicationFields({ error, replacing, applicationId, setApplicationId, privateKey, setPrivateKey }: {
  error: unknown; replacing: boolean; applicationId: string; setApplicationId: (v: string) => void; privateKey: string; setPrivateKey: (v: string) => void;
}) {
  const { t } = useTranslation();
  const [fileError, setFileError] = useState<string>();
  const fields = fieldErrorsOf(error);
  const fieldText = (path: string) => {
    const f = fields.find((x) => x.path === path);
    return f ? bankErrorText(t, f.code, f.message) : undefined;
  };
  return (
    <>
      <Field label={t('banks.application.id')} error={fieldText('applicationId')}
        tip={<InfoTip label={t('banks.application.id')}>{t('banks.application.id_tip')}</InfoTip>}>
        {(c) => <Input {...c} value={applicationId} autoComplete="off" onChange={(e) => setApplicationId(e.target.value)} />}
      </Field>
      <Field label={t('banks.application.private_key')} error={fileError ?? fieldText('privateKey')}
        hint={replacing ? t('banks.application.key_replace_hint') : t('banks.application.key_hint')}
        tip={<InfoTip label={t('banks.application.private_key')}>{t('banks.application.key_tip')}</InfoTip>}>
        {(c) => (
          <div className="space-y-2">
            <Textarea {...c} value={privateKey} rows={5} spellCheck={false} autoComplete="off" className="font-mono text-xs"
              onChange={(e) => setPrivateKey(e.target.value)} />
            <Input type="file" accept=".pem,.key,text/plain" aria-label={t('banks.application.key_file')}
              onChange={(e) => {
                const input = e.currentTarget;
                const file = input.files?.[0];
                setFileError(undefined);
                if (!file) return;
                if (file.size > MAX_KEY_BYTES) setFileError(t('banks.application.file_too_large'));
                else file.text().then(setPrivateKey, () => setFileError(t('banks.application.file_unreadable')));
                // The key is now in the field only: the input keeps no file.
                input.value = '';
              }} />
          </div>
        )}
      </Field>
    </>
  );
}

/** « Remplacer la clé »: the application's identifier and a new key, or the same key kept. */
function ApplicationDialog({ current, onClose, onDone }: { current: MyApplication; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [applicationId, setApplicationId] = useState(current.applicationId);
  const [privateKey, setPrivateKey] = useState('');
  const save = $api.useMutation('put', APPLICATION);
  return (
    <FormDialog title={t('banks.application.replace')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const key = privateKey.trim();
          save.mutate({ body: { applicationId: applicationId.trim(), ...(key ? { privateKey: key } : {}) } },
            { onSuccess: () => done(t('banks.application.replaced')) });
        }}>
          {save.error && fieldErrorsOf(save.error).length === 0 ? <ProblemMessage problem={presentBankError(t, save.error)} /> : null}
          <ApplicationFields error={save.error} replacing applicationId={applicationId} setApplicationId={setApplicationId}
            privateKey={privateKey} setPrivateKey={setPrivateKey} />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('banks.category_list.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
