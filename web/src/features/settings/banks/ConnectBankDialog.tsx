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
import { ASPSPS, CONNECTIONS } from './api';
import '@/locales/banks';

/** « Relier une banque »: the bank picked, the connection started, then the browser leaves for the bank's authorisation page. */
export function ConnectBankDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { goTo } = useSession();
  const banks = $api.useQuery('get', ASPSPS);
  const start = $api.useMutation('post', CONNECTIONS, { onSuccess: (started) => goTo(started.redirectUrl) });
  const [bank, setBank] = useState('');
  const failure = banks.error ?? start.error;

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
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            {/* Pending past success too: the browser is on its way to the bank. */}
            <Button type="submit" disabled={bank === '' || start.isPending || start.isSuccess}>{t('banks.connect_dialog.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
