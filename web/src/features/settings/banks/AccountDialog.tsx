import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import type { EnvelopeSummary } from '../../investments/api';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { ACCOUNT, ACCOUNTS, bankErrorText, CONNECTIONS, type BankAccount } from './api';
import '@/locales/banks';

/** The envelope kinds a bank account goes with (the server refuses any other, `banking.envelope_kind`). */
const ASSOCIABLE = ['checking', 'savings', 'other'];
/** Refusals about the envelope that come as a conflict or a plain 422, not on a field: shown on it all the same. */
const ON_ENVELOPE = ['banking.envelope_taken', 'banking.envelope_kind'];

/**
 * Where an account's balances go: an envelope the person writes (a card: the checking account it belongs to), whether it is
 * followed at all, and whether its balances are written straight away or proposed in « À valider ». Only what changed is sent.
 */
export function AccountDialog({ account, envelopes, parents, onClose, onDone }: {
  account: BankAccount; envelopes: EnvelopeSummary[]; parents: BankAccount[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const save = $api.useMutation('patch', ACCOUNT);
  const card = account.kind === 'card';
  const [envelopeId, setEnvelopeId] = useState(account.envelopeId ?? '');
  const [cardOf, setCardOf] = useState(account.cardOfAccountId ?? '');
  const [ignored, setIgnored] = useState(account.ignored);
  const [auto, setAuto] = useState(account.autoApplyBalance);

  const fields = fieldErrorsOf(save.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? bankErrorText(t, f.code, f.message) : undefined; };
  const code = save.error ? toProblem(save.error).code : null;
  const envelopeError = code && ON_ENVELOPE.includes(code) ? bankErrorText(t, code, '') : errorOf('envelopeId');
  const cardError = errorOf('cardOfAccountId');
  const unplaced = save.error && !envelopeError && !cardError;

  const submit = (done: (message: string) => void) => save.mutate({
    params: { path: { id: account.id } },
    body: {
      ...(!card && envelopeId !== (account.envelopeId ?? '') ? (envelopeId ? { envelopeId } : { clearEnvelope: true }) : {}),
      ...(card && cardOf !== (account.cardOfAccountId ?? '') ? (cardOf ? { cardOfAccountId: cardOf } : { clearCard: true }) : {}),
      ...(ignored !== account.ignored ? { ignored } : {}),
      ...(auto !== account.autoApplyBalance ? { autoApplyBalance: auto } : {}),
    },
  }, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', CONNECTIONS] });
      void queryClient.invalidateQueries({ queryKey: ['get', ACCOUNTS] });
      done(t('banks.account_dialog.saved', { name: account.name }));
    },
  });

  return (
    <FormDialog title={t('banks.edit_of', { name: account.name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          submit(done);
        }}>
          {unplaced ? <ProblemMessage problem={present(toProblem(save.error))} /> : null}
          {card ? (
            <Field label={t('banks.account_dialog.card_of')} error={cardError}>
              {(c) => (
                <SelectField {...c} value={cardOf} onValueChange={setCardOf} options={[
                  { value: '', label: t('banks.account_dialog.no_card') },
                  ...parents.filter((p) => p.id !== account.id).map((p) => ({ value: p.id, label: p.name })),
                ]} />
              )}
            </Field>
          ) : (
            <Field label={t('banks.account_dialog.envelope')} error={envelopeError}>
              {(c) => (
                <SelectField {...c} value={envelopeId} onValueChange={setEnvelopeId} options={[
                  { value: '', label: t('banks.account_dialog.none') },
                  ...envelopes.filter((e) => e.canWrite && !e.closed && ASSOCIABLE.includes(e.kind)).map((e) => ({ value: e.id, label: e.displayName })),
                ]} />
              )}
            </Field>
          )}
          <SwitchField label={t('banks.account_dialog.ignored')} checked={ignored} onCheckedChange={setIgnored} />
          <SwitchField label={t('banks.account_dialog.auto')} hint={t('banks.account_dialog.auto_hint')} checked={auto} onCheckedChange={setAuto} />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('banks.account_dialog.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
