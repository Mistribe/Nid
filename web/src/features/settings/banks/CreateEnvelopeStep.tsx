import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { invalidateInvestments } from '@/api/investments';
import { PERSONS } from '@/api/persons';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { Segmented } from '@/components/form/Segmented';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ACCOUNT_ENVELOPE, accountLabel, ACCOUNTS, ASSOCIABLE, bankErrorText, CONNECTIONS, type BankAccount } from '@/api/banking';
import '@/locales/banks';

/**
 * « Créer une enveloppe » in « Régler » (#346): a short step, prefilled from the bank account, whose envelope the server makes and links
 * in one go (`POST …/accounts/{id}/envelope`): a refused link leaves no envelope. Its currency is the account's.
 */
export function CreateEnvelopeStep({ account, bank, onBack, done }: {
  account: BankAccount; bank?: string; onBack: () => void; done: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const persons = $api.useQuery('get', PERSONS);
  const create = $api.useMutation('post', ACCOUNT_ENVELOPE);
  const label = accountLabel(t, account);
  const [displayName, setDisplayName] = useState((bank ? `${bank} ${label}` : label).slice(0, 100));
  const [kind, setKind] = useState(ASSOCIABLE.includes(account.kind) ? account.kind : 'checking');
  // « mine », « joint » or a dependent's id (#354): a child's account, theirs alone, which both adults manage.
  const [owners, setOwners] = useState('mine');
  const headingId = useId();
  const kindId = useId();
  const ownersId = useId();
  const ownersHintId = useId();
  // « Commun » only means something with another adult in the household.
  const joint = (persons.data?.filter((p) => p.kind === 'adult').length ?? 0) > 1;
  const dependents = persons.data?.filter((p) => p.kind === 'dependent') ?? [];
  const child = dependents.find((d) => d.id === owners);
  // A child is named by their first name, as on the owner chips.
  const firstName = (name: string) => name.split(' ')[0]!;

  const nameError = fieldErrorsOf(create.error).find((f) => f.path === 'displayName');
  const code = create.error ? toProblem(create.error).code : null;
  const submit = () => create.mutate({
    params: { path: { id: account.id } },
    body: { displayName: displayName.trim(), kind, ...(child ? { dependentId: child.id } : { joint: owners === 'joint' }) },
  }, {
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['get', CONNECTIONS] });
      void queryClient.invalidateQueries({ queryKey: ['get', ACCOUNTS] });
      void invalidateInvestments(queryClient);
      done(t('banks.create_envelope.done', { name: displayName.trim() }));
    },
  });

  return (
    <form aria-labelledby={headingId} noValidate className="space-y-4" onSubmit={(event) => {
      event.preventDefault();
      event.stopPropagation();
      submit();
    }}>
      <h3 id={headingId} className="font-medium">{t('banks.create_envelope.title')}</h3>
      {create.error && !nameError ? (
        code?.startsWith('banking.') ? <p role="alert" className="text-sm text-destructive">{bankErrorText(t, code, t('banks.create_envelope.failed'))}</p>
          : <ProblemMessage problem={present(toProblem(create.error))} />
      ) : null}
      <Field label={t('banks.create_envelope.name')} error={nameError ? messageFor(t, nameError) : undefined}>
        {(c) => <Input {...c} value={displayName} maxLength={100} onChange={(event) => setDisplayName(event.target.value)} />}
      </Field>
      <div className="space-y-1">
        <span id={kindId} className="block text-sm">{t('banks.create_envelope.kind')}</span>
        <Segmented aria-labelledby={kindId} value={kind} onValueChange={setKind}
          options={ASSOCIABLE.map((k) => ({ value: k, label: t(`investments.kind.${k}`, { defaultValue: k }) }))} />
      </div>
      {joint || dependents.length > 0 ? (
        <div className="space-y-1">
          <span id={ownersId} className="block text-sm">{t('banks.create_envelope.owners')}</span>
          <Segmented aria-labelledby={ownersId} aria-describedby={ownersHintId} value={owners} onValueChange={setOwners}
            options={[
              { value: 'mine', label: t('banks.create_envelope.mine') },
              ...(joint ? [{ value: 'joint', label: t('banks.create_envelope.joint') }] : []),
              ...dependents.map((d) => ({ value: d.id, label: t('banks.create_envelope.child', { name: firstName(d.displayName) }) })),
            ]} />
          <p id={ownersHintId} className="text-sm text-muted-foreground">
            {child ? t('banks.create_envelope.child_hint', { name: firstName(child.displayName) })
              : owners === 'joint' ? t('banks.create_envelope.joint_hint') : t('banks.create_envelope.mine_hint')}
          </p>
        </div>
      ) : null}
      <p className="text-sm text-muted-foreground">{t('banks.create_envelope.currency', { currency: account.currency })}</p>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={onBack}>{t('banks.create_envelope.back')}</Button>
        <Button type="submit" disabled={create.isPending}>{t('banks.create_envelope.submit')}</Button>
      </div>
    </form>
  );
}
