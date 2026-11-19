import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { ComboboxField } from '@/components/form/ComboboxField';
import { SelectField, type Option } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { EnvelopeSummary } from '@/api/investments';
import { PERSONS } from '@/api/persons';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { ACCOUNT, accountLabel, ACCOUNTS, ASSOCIABLE, bankErrorText, CONNECTIONS, type BankAccount } from '@/api/banking';
import { CreateEnvelopeStep } from './CreateEnvelopeStep';
import '@/locales/banks';

/** Refusals about the envelope that come as a conflict or a plain 422, not on a field: shown on it all the same. */
const ON_ENVELOPE = ['banking.envelope_taken', 'banking.envelope_kind'];

/** « Créer une enveloppe », last in the list: opens the step rather than picking an envelope. */
const CREATE = '__create__';

/** Past this many choices the list takes a search box. */
const LONG = 8;

/**
 * The envelopes an account may go with (#346), each named with its owners and kind (two « Livret A » told apart): those that fit the
 * account (same kind, same currency) first, the others under « Autres enveloppes »; one another bank account holds is not offered.
 */
export function envelopeOptions(t: TFunction, account: BankAccount, envelopes: EnvelopeSummary[], taken: readonly string[], names: ReadonlyMap<string, string>): Option[] {
  const list = new Intl.ListFormat('fr', { type: 'conjunction' });
  const offered = envelopes.filter((e) => e.canWrite && !e.closed && ASSOCIABLE.includes(e.kind) && (!taken.includes(e.id) || e.id === account.envelopeId));
  const fits = (e: EnvelopeSummary) => e.kind === account.kind && e.currency === account.currency;
  const option = (e: EnvelopeSummary, group: string): Option => {
    const owners = list.format(e.owners.flatMap((o) => names.get(o.personId) ?? []));
    const label = [e.displayName, owners, t(`investments.kind.${e.kind}`, { defaultValue: e.kind }), e.currency === account.currency ? '' : e.currency].filter(Boolean).join(' · ');
    return { value: e.id, label, group };
  };
  const matching = offered.filter(fits);
  const others = offered.filter((e) => !fits(e));
  return [
    ...matching.map((e) => option(e, t('banks.account_dialog.matching'))),
    ...others.map((e) => option(e, t('banks.account_dialog.others'))),
  ];
}

/**
 * Where an account's balances go: an envelope the person writes (a card: the checking account it belongs to), whether it is
 * followed at all, and whether its balances are written straight away or proposed in « À valider ». Only what changed is sent.
 */
export function AccountDialog({ account, envelopes, taken = [], bank, parents, onClose, onDone }: {
  account: BankAccount; envelopes: EnvelopeSummary[]; taken?: readonly string[]; bank?: string; parents: BankAccount[];
  onClose: () => void; onDone: (message: string) => void;
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
  const [creating, setCreating] = useState(false);
  const persons = $api.useQuery('get', PERSONS);
  // A child is named by their first name, as on the owner chips.
  const names = new Map((persons.data ?? []).map((p) => [p.id, p.kind === 'adult' ? p.displayName : p.displayName.split(' ')[0]!]));
  const options: Option[] = [
    { value: '', label: t('banks.account_dialog.none') },
    ...envelopeOptions(t, account, envelopes, taken, names),
    { value: CREATE, label: t('banks.account_dialog.create') },
  ];
  const pickEnvelope = (v: string) => (v === CREATE ? setCreating(true) : setEnvelopeId(v));
  // The household's own name; empty: the bank's, shown as the placeholder.
  const [rename, setRename] = useState(account.renamed ? account.name : '');
  const name = accountLabel(t, account);
  const bankLabel = accountLabel(t, { ...account, name: account.bankName });

  const fields = fieldErrorsOf(save.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? bankErrorText(t, f.code, f.message) : undefined; };
  const code = save.error ? toProblem(save.error).code : null;
  const envelopeError = code && ON_ENVELOPE.includes(code) ? bankErrorText(t, code, '') : errorOf('envelopeId');
  const cardError = errorOf('cardOfAccountId');
  const nameError = errorOf('name');
  const unplaced = save.error && !envelopeError && !cardError && !nameError;
  const typed = rename.trim();

  const submit = (done: (message: string) => void) => save.mutate({
    params: { path: { id: account.id } },
    body: {
      ...(typed !== (account.renamed ? account.name : '') ? (typed ? { name: typed } : { clearName: true }) : {}),
      ...(!card && envelopeId !== (account.envelopeId ?? '') ? (envelopeId ? { envelopeId } : { clearEnvelope: true }) : {}),
      ...(card && cardOf !== (account.cardOfAccountId ?? '') ? (cardOf ? { cardOfAccountId: cardOf } : { clearCard: true }) : {}),
      ...(ignored !== account.ignored ? { ignored } : {}),
      ...(auto !== account.autoApplyBalance ? { autoApplyBalance: auto } : {}),
    },
  }, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', CONNECTIONS] });
      void queryClient.invalidateQueries({ queryKey: ['get', ACCOUNTS] });
      done(t('banks.account_dialog.saved', { name: typed || bankLabel }));
    },
  });

  return (
    <FormDialog title={t('banks.edit_of', { name })} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (creating ? <CreateEnvelopeStep account={account} bank={bank} onBack={() => setCreating(false)} done={done} /> : (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          submit(done);
        }}>
          {unplaced ? <ProblemMessage problem={present(toProblem(save.error))} /> : null}
          <Field label={t('banks.account_dialog.name')} hint={t('banks.account_dialog.name_hint')} error={nameError}>
            {(c) => <Input {...c} value={rename} maxLength={100} placeholder={bankLabel}
              onChange={(event) => setRename(event.target.value)} />}
          </Field>
          {rename ? (
            <Button type="button" variant="link" className="h-auto p-0" onClick={() => setRename('')}>{t('banks.account_dialog.reset_name')}</Button>
          ) : null}
          {card ? (
            <Field label={t('banks.account_dialog.card_of')} error={cardError}>
              {(c) => (
                <SelectField {...c} value={cardOf} onValueChange={setCardOf} options={[
                  { value: '', label: t('banks.account_dialog.no_card') },
                  ...parents.filter((p) => p.id !== account.id).map((p) => ({ value: p.id, label: accountLabel(t, p) })),
                ]} />
              )}
            </Field>
          ) : (
            <Field label={t('banks.account_dialog.envelope')} error={envelopeError}>
              {(c) => (options.length > LONG
                ? <ComboboxField {...c} value={envelopeId} onValueChange={pickEnvelope} options={options}
                  searchPlaceholder={t('banks.account_dialog.search')} empty={t('banks.account_dialog.no_envelope')} />
                : <SelectField {...c} value={envelopeId} onValueChange={pickEnvelope} options={options} />)}
            </Field>
          )}
          <SwitchField label={t('banks.account_dialog.ignored')} checked={ignored} onCheckedChange={setIgnored} />
          <SwitchField label={t('banks.account_dialog.auto')} hint={t('banks.account_dialog.auto_hint')} checked={auto} onCheckedChange={setAuto} />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('banks.account_dialog.save')}</Button>
          </div>
        </form>
      ))}
    </FormDialog>
  );
}
