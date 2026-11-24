import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ASPSPS, CONNECTIONS, PORTAL_URL, RENEW } from '@/api/banking';
import '@/locales/banks';

/**
 * « Relier une banque »: the bank picked, the connection started, then the browser leaves for the bank's authorisation page. A bank the
 * person is already connected to is offered to renew instead (#381): the same consent asked again rather than a second connection.
 */
export function ConnectBankDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { goTo } = useSession();
  const banks = $api.useQuery('get', ASPSPS);
  const connections = $api.useQuery('get', CONNECTIONS);
  const start = $api.useMutation('post', CONNECTIONS, { onSuccess: (started) => goTo(started.redirectUrl) });
  const renew = $api.useMutation('post', RENEW, { onSuccess: (started) => goTo(started.redirectUrl) });
  const [bank, setBank] = useState('');
  const existing = connections.data?.find((c) => c.aspspName === bank);
  const failure = banks.error ?? start.error ?? renew.error;
  // Pending past success too: the browser is on its way to the bank.
  const leaving = start.isPending || start.isSuccess || renew.isPending || renew.isSuccess;

  return (
    <FormDialog title={t('banks.connect')} onClose={onClose} onDone={onClose}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          start.mutate({ body: { aspspName: bank } });
        }}>
          {failure ? <ProblemMessage problem={present(toProblem(failure))} /> : null}
          <Field label={t('banks.connect_dialog.bank')} hint={t('banks.connect_dialog.hint')}>
            {(c) => (
              <ComboboxField {...c} value={bank} onValueChange={setBank} disabled={!banks.data}
                options={(banks.data ?? []).map((b) => ({ value: b.name, label: b.name }))}
                searchPlaceholder={t('banks.connect_dialog.search')} empty={t('banks.connect_dialog.no_bank')} />
            )}
          </Field>
          <p className="text-sm text-muted-foreground">
            {t('banks.connect_dialog.portal')}{' '}
            <a href={PORTAL_URL} target="_blank" rel="noreferrer" className="underline underline-offset-4">{t('banks.wizard.open_portal')}</a>
          </p>
          {existing ? <p role="status" className="text-sm">{t('banks.connect_dialog.existing', { bank })}</p> : null}
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            {existing ? (
              <>
                <Button type="submit" variant="outline" disabled={leaving}>{t('banks.connect_dialog.another')}</Button>
                <Button type="button" disabled={leaving} onClick={() => renew.mutate({ params: { path: { id: existing.id } } })}>{t('banks.renew')}</Button>
              </>
            ) : (
              <Button type="submit" disabled={bank === '' || leaving}>{t('banks.connect_dialog.submit')}</Button>
            )}
          </div>
        </form>
      )}
    </FormDialog>
  );
}
