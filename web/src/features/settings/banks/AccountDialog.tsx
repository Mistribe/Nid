import { useQueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { ComboboxField } from '@/components/form/ComboboxField';
import { SelectField, type Option } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SwitchField } from '@/components/page/SwitchField';
import { Segmented } from '@/components/form/Segmented';
import { InfoTip } from '@/components/InfoTip';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import type { EnvelopeSummary } from '@/api/investments';
import { PERSONS } from '@/api/persons';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { formatLocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { StatLine } from '@/components/page/StatLine';
import { ACCOUNT, ACCOUNT_OVERVIEW, accountKind, accountLabel, ACCOUNTS, ASSOCIABLE, bankErrorText, CONNECTIONS, maskedIban, type BankAccount, type BankTransaction } from '@/api/banking';
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
 * #366: the checking accounts a card may go with, named as the household knows them: the envelope and its owners first, then the
 * bank's label (« Compte joint · Alice et Bob · •••• 1234 »); those on an envelope come first, the others keep the bank's label.
 */
export function parentOptions(t: TFunction, card: BankAccount, parents: BankAccount[], envelopes: EnvelopeSummary[], names: ReadonlyMap<string, string>): Option[] {
  const list = new Intl.ListFormat('fr', { type: 'conjunction' });
  const labelled = parents.filter((p) => p.id !== card.id).map((p) => {
    const envelope = envelopes.find((e) => e.id === p.envelopeId);
    const owners = envelope ? list.format(envelope.owners.flatMap((o) => names.get(o.personId) ?? [])) : '';
    return { value: p.id, label: [envelope?.displayName, owners, accountLabel(t, p)].filter(Boolean).join(' · '), associated: !!envelope };
  });
  return [...labelled.filter((o) => o.associated), ...labelled.filter((o) => !o.associated)].map(({ value, label }) => ({ value, label }));
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
  const overview = $api.useQuery('get', ACCOUNT_OVERVIEW, { params: { path: { id: account.id } } });
  const [envelopeId, setEnvelopeId] = useState(account.envelopeId ?? '');
  // Null until the person picks: the attached account, else the server's suggestion (#353), saved only with « Enregistrer ».
  const [picked, setPicked] = useState<string | null>(null);
  const suggested = overview.data?.suggestedCardOf ?? null;
  const cardOf = picked ?? account.cardOfAccountId ?? suggested ?? '';
  const [ignored, setIgnored] = useState(account.ignored);
  const [auto, setAuto] = useState(account.autoApplyBalance);
  // #362: a card's minimum usage, the household's rule: off, 1 payment a month, or more (typed).
  const [usage, setUsage] = useState(account.minPayments == null ? 'off' : account.minPayments === 1 ? 'one' : 'many');
  const [many, setMany] = useState(account.minPayments != null && account.minPayments > 1 ? String(account.minPayments) : '2');
  const [withdrawals, setWithdrawals] = useState(account.countWithdrawals ?? false);
  const usageId = useId();
  const usageHintId = useId();
  const minPayments = usage === 'off' ? null : usage === 'one' ? 1 : Number(many);
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
  const usageError = errorOf('minPayments');
  const unplaced = save.error && !envelopeError && !cardError && !nameError && !usageError;
  const typed = rename.trim();

  const submit = (done: (message: string) => void) => save.mutate({
    params: { path: { id: account.id } },
    body: {
      ...(typed !== (account.renamed ? account.name : '') ? (typed ? { name: typed } : { clearName: true }) : {}),
      ...(!card && envelopeId !== (account.envelopeId ?? '') ? (envelopeId ? { envelopeId } : { clearEnvelope: true }) : {}),
      ...(card && cardOf !== (account.cardOfAccountId ?? '') ? (cardOf ? { cardOfAccountId: cardOf } : { clearCard: true }) : {}),
      ...(ignored !== account.ignored ? { ignored } : {}),
      ...(auto !== account.autoApplyBalance ? { autoApplyBalance: auto } : {}),
      ...(card && minPayments !== (account.minPayments ?? null) ? (minPayments === null ? { clearMinPayments: true } : { minPayments }) : {}),
      ...(card && withdrawals !== (account.countWithdrawals ?? false) ? { countWithdrawals: withdrawals } : {}),
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
          <Identity account={account} />
          <Recent lines={overview.data?.recent} />
          <Field label={t('banks.account_dialog.name')} hint={t('banks.account_dialog.name_hint')} error={nameError}>
            {(c) => <Input {...c} value={rename} maxLength={100} placeholder={bankLabel}
              onChange={(event) => setRename(event.target.value)} />}
          </Field>
          {rename ? (
            <Button type="button" variant="link" className="h-auto p-0" onClick={() => setRename('')}>{t('banks.account_dialog.reset_name')}</Button>
          ) : null}
          {card ? (
            <Field label={t('banks.account_dialog.card_of')} error={cardError}
              hint={!account.cardOfAccountId && suggested && cardOf === suggested ? t('banks.account_dialog.suggested') : undefined}>
              {(c) => (
                <SelectField {...c} value={cardOf} onValueChange={setPicked} options={[
                  { value: '', label: t('banks.account_dialog.no_card') },
                  ...parentOptions(t, account, parents, envelopes, names),
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
          {card ? (
            <div className="space-y-2">
              <span id={usageId} className="flex items-center gap-1 text-sm">
                {t('banks.account_dialog.usage')}
                <InfoTip label={t('banks.account_dialog.usage')}>{t('banks.account_dialog.usage_tip')}</InfoTip>
              </span>
              <Segmented aria-labelledby={usageId} aria-describedby={usageHintId} value={usage} onValueChange={setUsage} options={[
                { value: 'off', label: t('banks.account_dialog.usage_off') },
                { value: 'one', label: t('banks.account_dialog.usage_one') },
                { value: 'many', label: t('banks.account_dialog.usage_many') },
              ]} />
              <p id={usageHintId} className="text-sm text-muted-foreground">{t('banks.account_dialog.usage_hint')}</p>
              {usage === 'many' ? (
                <Field label={t('banks.account_dialog.usage_count')} error={usageError}>
                  {(c) => <Input {...c} inputMode="numeric" className="w-24" value={many} onChange={(event) => setMany(event.target.value)} />}
                </Field>
              ) : usageError ? <p role="alert" className="text-sm text-destructive">{usageError}</p> : null}
              {usage !== 'off' ? (
                <SwitchField label={t('banks.account_dialog.usage_withdrawals')} checked={withdrawals} onCheckedChange={setWithdrawals} />
              ) : null}
            </div>
          ) : null}
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

/** #353: what the bank says of the account, so two cards of one connection are told apart: number, holder, product, debit, last balance. */
function Identity({ account }: { account: BankAccount }) {
  const { t } = useTranslation();
  const items = [
    account.ibanMasked ? { label: t('banks.account_dialog.number'), value: maskedIban(account.ibanMasked) } : null,
    account.holder ? { label: t('banks.account_dialog.holder'), value: account.holder } : null,
    account.product ? { label: t('banks.account_dialog.product'), value: account.product } : null,
    { label: t('banks.account_dialog.type'), value: accountKind(t, account) },
    account.lastBalance && account.lastBalanceOn
      ? { label: t('banks.account_dialog.last_balance'), value: formatCurrency(account.lastBalance, account.currency), hint: formatLocalDate(account.lastBalanceOn) }
      : null,
  ].filter((item) => item !== null);
  return (
    <section aria-label={t('banks.account_dialog.identity')}>
      <StatLine items={items} />
    </section>
  );
}

/** Its latest lines, as the server gives them: « the card that paid the bakery yesterday » is found faster than by a number. */
function Recent({ lines }: { lines?: BankTransaction[] }) {
  const { t } = useTranslation();
  if (!lines) return null;
  return (
    <section aria-label={t('banks.account_dialog.recent')} className="space-y-1">
      <h3 className="text-sm font-medium">{t('banks.account_dialog.recent')}</h3>
      {lines.length === 0 ? <p className="text-sm text-muted-foreground">{t('banks.account_dialog.no_recent')}</p> : (
        <ul className="space-y-0.5 text-sm">
          {lines.map((l) => (
            <li key={l.id} className="flex justify-between gap-3">
              <span className="min-w-0 truncate"><span className="text-muted-foreground">{formatLocalDate(l.bookedOn)}</span> {l.label}</span>
              <span className="tabular-nums">{formatCurrency(l.amount, l.currency)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
