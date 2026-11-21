import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import type { components } from '@/api/schema';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { FormDialog } from '@/components/FormDialog';
import { InfoTip } from '@/components/InfoTip';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { ConnectionTestResult } from './ConnectionTestResult';
import { APPLICATION, bankErrorText, presentBankError } from '@/api/banking';
import { formatInstantDate } from '@/lib/format';
import '@/locales/banks';

const TEST = '/api/v1/banking/application/test' as const;

type Test = components['schemas']['BankingConnectionTestView'];
const MAX_KEY_BYTES = 16 * 1024;

/**
 * Réglages › Banques › Mon application (#382): the Enable Banking application this adult declares instead of the installation's.
 * The private key is write-only: sent once, never read back, never shown.
 */
export default function ApplicationTab({ focus = false }: { focus?: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const mine = $api.useQuery('get', APPLICATION);
  const [dialog, setDialog] = useState<'declare' | 'replace' | null>(null);
  const [asking, setAsking] = useState(false);
  const [tested, setTested] = useState<Test | null>(null);
  const menu = useRef<HTMLButtonElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const changed = () => queryClient.invalidateQueries({ queryKey: ['get', APPLICATION] });
  const remove = $api.useMutation('delete', APPLICATION, {
    onSuccess: () => { toast.success(t('banks.application.removed')); setTested(null); void changed(); },
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
  const done = (message: string) => { toast.success(message); setTested(null); setDialog(null); void changed(); };

  return (
    <Section id="application" title={t('banks.application.title')} headingRef={heading}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('banks.application.intro')}</p>
      {view ? (
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
                { label: t('banks.application.replace'), onSelect: () => setDialog('replace') },
                { label: t('banks.application.remove'), onSelect: () => setAsking(true), destructive: true },
              ]} />
            </span>
          </div>
          <div aria-live="polite" className="space-y-1">{tested ? <ConnectionTestResult result={tested} /> : null}</div>
        </div>
      ) : <div><Button type="button" onClick={() => setDialog('declare')}>{t('banks.application.declare')}</Button></div>}
      {dialog ? <ApplicationDialog current={view ?? null} onClose={() => setDialog(null)} onDone={done} /> : null}
      {asking ? (
        <ConfirmFromMenu label={t('banks.application.remove')} confirmation={t('banks.application.remove_confirm')} pending={remove.isPending} returnFocus={menu}
          onClose={() => setAsking(false)} onConfirm={() => remove.mutate({})} />
      ) : null}
    </Section>
  );
}

/** The identifier and the key; with an application already declared, an empty key keeps the one loaded. The key lives in this dialog's state only. */
function ApplicationDialog({ current, onClose, onDone }: {
  current: components['schemas']['MyApplicationView'] | null; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [applicationId, setApplicationId] = useState(current?.applicationId ?? '');
  const [privateKey, setPrivateKey] = useState('');
  const [fileError, setFileError] = useState<string>();
  const save = $api.useMutation('put', APPLICATION);
  const fields = fieldErrorsOf(save.error);
  const fieldText = (path: string) => {
    const f = fields.find((x) => x.path === path);
    return f ? bankErrorText(t, f.code, f.message) : undefined;
  };
  const replacing = current !== null;
  return (
    <FormDialog title={replacing ? t('banks.application.replace') : t('banks.application.declare')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          const key = privateKey.trim();
          save.mutate({ body: { applicationId: applicationId.trim(), ...(key ? { privateKey: key } : {}) } },
            { onSuccess: () => done(t(replacing ? 'banks.application.replaced' : 'banks.application.declared')) });
        }}>
          {save.error && fields.length === 0 ? <ProblemMessage problem={presentBankError(t, save.error)} /> : null}
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
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('banks.category_list.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
