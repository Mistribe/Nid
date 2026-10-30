import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { BanksSearch } from '@/app/router';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { RowActions } from '@/components/page/RowActions';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { formatInstant } from '../../admin/format';
import { ENVELOPES, invalidateInvestments, type EnvelopeSummary } from '../../investments/api';
import { SettingsFrame } from '../SettingsFrame';
import { AccountDialog } from './AccountDialog';
import { ConnectBankDialog } from './ConnectBankDialog';
import { ACCOUNTS, bankErrorText, CONNECTION, CONNECTIONS, RENEW, SYNC, useBankAccounts, useBankingStatus, type BankAccount, type Connection } from './api';
import '@/locales/banks';

const PROPOSALS = '/api/v1/core/proposals' as const;
const day = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short' });

const refresh = (queryClient: QueryClient) => Promise.all([
  queryClient.invalidateQueries({ queryKey: ['get', CONNECTIONS] }),
  queryClient.invalidateQueries({ queryKey: ['get', ACCOUNTS] }),
]).then(() => undefined);

/** The « Synchroniser » mutation (D6): `{}` reads all the person's connections, `{ connectionId }` one; `announce` says how it went. */
function useSync(announce: (message: string) => void) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  return $api.useMutation('post', SYNC, {
    onSuccess: () => {
      announce(t('banks.synced'));
      // The balances became values or proposals.
      void refresh(queryClient);
      void invalidateInvestments(queryClient);
      void queryClient.invalidateQueries({ queryKey: ['get', PROPOSALS] });
    },
    // Not a failure to explain: the sync asked for is already under way.
    onError: (error) => { if (toProblem(error).code === 'banking.sync_running') announce(t('banks.error.banking_sync_running')); },
  });
}

/** Réglages › Banques (bank sync spec §8): the person's bank connections, each with its accounts, then the accounts none reads any more. */
export function BanksPage() {
  const { t } = useTranslation();
  const status = useBankingStatus();
  const on = status.data?.enabled === true && status.data.configured;
  return (
    <SettingsFrame title={t('banks.title')} actions={on ? <SyncAll /> : null}>
      {status.error ? <ProblemMessage problem={present(toProblem(status.error))} />
        : !status.data ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.loading')}</span></Skeleton>
        : !status.data.enabled ? <p>{t('banks.disabled')}</p>
        : !status.data.configured ? <p>{t('banks.not_configured')}</p>
        : <Connections />}
    </SettingsFrame>
  );
}

/** « Synchroniser tout » (D6), once the person has a connection. */
function SyncAll() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const connections = $api.useQuery('get', CONNECTIONS);
  const [announcement, setAnnouncement] = useState('');
  const sync = useSync(setAnnouncement);
  const failed = sync.error && toProblem(sync.error).code !== 'banking.sync_running' ? bankErrorText(t, toProblem(sync.error).code ?? '', t('banks.sync_failed')) : '';
  if (!connections.data?.length) return null;
  return (
    <>
      <span role="status" aria-live="polite" className="text-sm">{failed || announcement}</span>
      <Button type="button" variant="outline" disabled={sync.isPending} onClick={() => { setAnnouncement(''); sync.mutate({ body: {} }); }}>{t('banks.sync_all')}</Button>
    </>
  );
}

function Connections() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: BanksSearch = useSearch({ strict: false });
  // What the bank's callback left in the address, read once: the address drops it so a reload does not repeat it.
  const [arrival] = useState(search);
  const offered = useRef(false);
  const [saved, setSaved] = useState(arrival.connected ? t('banks.connected') : '');
  const [connecting, setConnecting] = useState(false);
  const [editing, setEditing] = useState<BankAccount | null>(null);
  const connections = $api.useQuery('get', CONNECTIONS);
  const envelopes = $api.useQuery('get', ENVELOPES);
  const readable = useBankAccounts();

  useEffect(() => {
    if (arrival.connected || arrival.error) void navigate({ to: '/settings/banks', search: {}, replace: true });
  }, [arrival, navigate]);
  // Back from the bank: its first account not placed yet opens, so the person says where it goes.
  useEffect(() => {
    if (offered.current || !arrival.connected || !connections.data) return;
    offered.current = true;
    const fresh = connections.data.find((c) => c.id === arrival.connected)?.accounts
      .find((a) => a.canWrite && !a.envelopeId && !a.cardOfAccountId && !a.ignored);
    if (fresh) setEditing(fresh);
  }, [arrival, connections.data]);

  const listed = connections.data?.flatMap((c) => c.accounts) ?? [];
  // Accounts no connection of the person's lists: their connection was deleted, or a renewal left them out (spec §8).
  const unsynced = connections.data ? (readable ?? []).filter((a) => !a.synced && !listed.some((l) => l.id === a.id)) : [];
  const all = [...listed, ...unsynced];
  const edit = (account: BankAccount) => { setSaved(''); setEditing(account); };
  const announce = (message: string) => { setEditing(null); setConnecting(false); setSaved(message); };
  const connect = <Button type="button" onClick={() => { setSaved(''); setConnecting(true); }}>{t('banks.connect')}</Button>;

  return (
    <div className="space-y-6">
      <p role="status" aria-live="polite" className="text-sm">{saved}</p>
      {arrival.error ? <p role="alert" className="text-sm text-destructive">{bankErrorText(t, arrival.error, t('banks.error.generic'))}</p> : null}
      {connections.error ? <ProblemMessage problem={present(toProblem(connections.error))} /> : null}
      {!connections.data && !connections.error ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.loading')}</span></Skeleton> : null}
      {connections.data?.length === 0 ? <EmptyState title={t('banks.empty_title')} body={t('banks.empty_body')} action={connect} /> : null}
      {connections.data && connections.data.length > 0 ? (
        <>
          <div>{connect}</div>
          {connections.data.map((c) => (
            <ConnectionSection key={c.id} connection={c} all={all} envelopes={envelopes.data?.items ?? []} onEdit={edit} onDeleted={setSaved} />
          ))}
        </>
      ) : null}
      {unsynced.length > 0 ? (
        <Section title={t('banks.unsynced')}>
          <p className="text-sm text-muted-foreground">{t('banks.unsynced_hint')}</p>
          <AccountTable caption={t('banks.unsynced')} accounts={unsynced} all={all} envelopes={envelopes.data?.items ?? []} onEdit={edit} />
        </Section>
      ) : null}
      {connecting ? <ConnectBankDialog onClose={() => setConnecting(false)} /> : null}
      {editing ? (
        <AccountDialog account={editing} envelopes={envelopes.data?.items ?? []}
          parents={all.filter((a) => a.kind === 'checking' && a.canWrite)} onClose={() => setEditing(null)} onDone={announce} />
      ) : null}
    </div>
  );
}

function ConnectionSection({ connection, all, envelopes, onEdit, onDeleted }: {
  connection: Connection; all: BankAccount[]; envelopes: EnvelopeSummary[]; onEdit: (account: BankAccount) => void; onDeleted: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { goTo } = useSession();
  const queryClient = useQueryClient();
  const [announcement, setAnnouncement] = useState('');
  const bank = connection.aspspName;
  const path = { params: { path: { id: connection.id } } };
  const sync = useSync(setAnnouncement);
  const renew = $api.useMutation('post', RENEW, { onSuccess: (started) => goTo(started.redirectUrl) });
  const remove = $api.useMutation('delete', CONNECTION);
  const failed = [sync, renew].find((m) => m.error && toProblem(m.error).code !== 'banking.sync_running')?.error;

  return (
    <Section title={bank} announcement={announcement}>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
        <span className="font-medium text-foreground">{t(`banks.status.${connection.status}`, { defaultValue: connection.status })}</span>
        {connection.validUntil ? <span>{t('banks.expires', { date: day.format(new Date(connection.validUntil)) })}</span> : null}
        <span>{connection.lastSyncedAt ? t('banks.last_sync', { date: formatInstant(connection.lastSyncedAt) }) : t('banks.never_synced')}</span>
        <RowActions menuId={`bank-${connection.id}`} menuLabel={t('banks.actions_of', { bank })}
          items={[
            { label: t('banks.sync'), onSelect: () => { setAnnouncement(''); sync.mutate({ body: { connectionId: connection.id } }); } },
            { label: t('banks.renew'), onSelect: () => { setAnnouncement(''); renew.mutate(path); } },
          ]}
          confirmation={t('banks.delete_confirm', { bank })} remove={remove}
          onDeleteStarted={() => setAnnouncement('')}
          onConfirm={() => remove.mutate(path, { onSuccess: () => { onDeleted(t('banks.deleted', { bank })); void refresh(queryClient); } })}
          present={(_, error) => present(toProblem(error))} invalidate={refresh} />
      </div>
      {failed ? <ProblemMessage problem={present(toProblem(failed))} /> : null}
      {connection.accounts.length > 0 ? (
        <AccountTable caption={t('banks.accounts', { bank })} accounts={connection.accounts} all={all} envelopes={envelopes} onEdit={onEdit} />
      ) : null}
    </Section>
  );
}

/** Accounts as a dense table (a list on a phone): name and masked IBAN, kind, where their balances go, last balance, « Régler ». */
function AccountTable({ caption, accounts, all, envelopes, onEdit }: {
  caption: string; accounts: BankAccount[]; all: BankAccount[]; envelopes: EnvelopeSummary[]; onEdit: (account: BankAccount) => void;
}) {
  const { t } = useTranslation();
  const envelopeOf = (a: BankAccount) =>
    a.ignored ? t('banks.ignored')
      : a.cardOfAccountId ? t('banks.card_of', { name: all.find((p) => p.id === a.cardOfAccountId)?.name ?? '' })
      : envelopes.find((e) => e.id === a.envelopeId)?.displayName ?? '—';

  const name = (a: BankAccount) => (
    <span className="whitespace-normal">
      <span className="block">{a.name}</span>
      {a.ibanMasked ? <span className="block text-sm text-muted-foreground">{a.ibanMasked}</span> : null}
    </span>
  );
  const kind = (a: BankAccount) => t(`banks.kind.${a.kind}`, { defaultValue: a.kind });
  const balance = (a: BankAccount) => (a.lastBalance && a.lastBalanceOn
    ? t('banks.balance_on', { amount: formatCurrency(a.lastBalance, a.currency), date: formatLocalDate(a.lastBalanceOn) }) : '—');
  const edit = (a: BankAccount) => (a.canWrite ? (
    <Button type="button" variant="outline" size="sm" aria-label={t('banks.edit_of', { name: a.name })} onClick={() => onEdit(a)}>{t('banks.edit')}</Button>
  ) : null);

  return (
    <SortableTable<BankAccount> caption={caption} rows={accounts} rowKey={(a) => a.id}
      columns={[
        { id: 'name', header: t('banks.account'), sort: (a) => a.name, cell: name },
        { id: 'kind', header: t('banks.kind_label'), cell: kind },
        { id: 'target', header: t('banks.envelope'), cell: envelopeOf },
        { id: 'balance', header: t('banks.balance'), cell: balance },
        { id: 'actions', header: t('banks.actions'), cell: edit },
      ]}
      mobile={(a) => (
        <div className="space-y-1">
          <div className="flex items-start justify-between gap-2">{name(a)}{edit(a)}</div>
          <div className="flex flex-wrap justify-between gap-x-3 text-sm text-muted-foreground"><span>{kind(a)} · {envelopeOf(a)}</span><span className="tabular-nums">{balance(a)}</span></div>
        </div>
      )} />
  );
}
