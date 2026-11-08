import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
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
import { formatInstant, formatInstantDay } from '@/lib/format';
import { ENVELOPES, invalidateInvestments, type EnvelopeSummary } from '../../investments/api';
import { LOANS } from '../../loans/api';
import { PROPERTIES } from '../../properties/api';
import { SettingsFrame } from '../SettingsFrame';
import { AccountDialog } from './AccountDialog';
import { ConnectBankDialog } from './ConnectBankDialog';
import {
  ACCOUNTS, bankErrorText, CONNECTION, CONNECTIONS, RENEW, RULE, RULES, SYNC, useBankAccounts, useBankingStatus, type BankAccount, type BankRule, type Connection,
} from './api';
import '@/locales/banks';

// Loaded when opened: most visits import no statement.
const ImportStatementDialog = lazy(() => import('../../investments/operations/ImportStatementDialog'));

const PROPOSALS = '/api/v1/core/proposals' as const;
const day = new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short' });

const refresh = (queryClient: QueryClient) => Promise.all([
  queryClient.invalidateQueries({ queryKey: ['get', CONNECTIONS] }),
  queryClient.invalidateQueries({ queryKey: ['get', ACCOUNTS] }),
]).then(() => undefined);

/** The « Synchroniser » mutation (D6): `{}` reads all the person's connections, `{ connectionId }` one; a toast says how it went. */
function useSync() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  return $api.useMutation('post', SYNC, {
    onSuccess: () => {
      toast.success(t('banks.synced'));
      // The balances became values or proposals.
      void refresh(queryClient);
      void invalidateInvestments(queryClient);
      void queryClient.invalidateQueries({ queryKey: ['get', PROPOSALS] });
    },
    // Not a failure to explain: the sync asked for is already under way.
    onError: (error) => { if (toProblem(error).code === 'banking.sync_running') toast.info(t('banks.error.banking_sync_running')); },
  });
}

/** Réglages › Banques (bank sync spec §8): the person's bank connections, each with its accounts, then the accounts none reads any more. */
export function BanksPage() {
  const { t } = useTranslation();
  const status = useBankingStatus();
  const on = status.data?.enabled === true && status.data.configured;
  return (
    <SettingsFrame title={t('banks.title')} actions={status.data?.enabled ? <>{on ? <SyncAll /> : null}<ImportStatement /></> : null}>
      {status.error ? <ProblemMessage problem={present(toProblem(status.error))} />
        : !status.data ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.loading')}</span></Skeleton>
        : !status.data.enabled ? <p>{t('banks.disabled')}</p>
        : <div className="space-y-8"><Connections configured={status.data.configured} /><Rules /></div>}
    </SettingsFrame>
  );
}

/** « Synchroniser tout » (D6), once the person has a connection. */
function SyncAll() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const connections = $api.useQuery('get', CONNECTIONS);
  const sync = useSync();
  const failed = (error: unknown) => {
    const code = toProblem(error).code;
    if (code !== 'banking.sync_running') toast.error(bankErrorText(t, code ?? '', t('banks.sync_failed')));
  };
  if (!connections.data?.length) return null;
  return <Button type="button" variant="outline" disabled={sync.isPending} onClick={() => sync.mutate({ body: {} }, { onError: failed })}>{t('banks.sync_all')}</Button>;
}

/** « Importer un relevé » (spec §5.2) on no envelope: banking on is enough, a household may use statement files alone. */
function ImportStatement() {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [landOn, setLandOn] = useState(false);
  const button = useRef<HTMLButtonElement>(null);
  // Once the dialog handed itself back, the focus returns to its button, never <body>.
  useEffect(() => { if (landOn) { button.current?.focus(); setLandOn(false); } }, [landOn]);
  return (
    <>
      <Button ref={button} type="button" variant="outline" onClick={() => setOpen(true)}>{t('banks.import.open')}</Button>
      {open ? (
        <Suspense fallback={null}>
          <ImportStatementDialog onClose={() => setOpen(false)} onDone={(message) => { setOpen(false); toast.success(message); setLandOn(true); }} />
        </Suspense>
      ) : null}
    </>
  );
}

/** Without Enable Banking (`configured` false) no bank can be connected: the accounts imported from statement files are listed all the same. */
function Connections({ configured }: { configured: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: BanksSearch = useSearch({ strict: false });
  // What the bank's callback left in the address, read once: the address drops it so a reload does not repeat it.
  const [arrival] = useState(search);
  const offered = useRef(false);
  const [connecting, setConnecting] = useState(false);
  const [editing, setEditing] = useState<BankAccount | null>(null);
  const connections = $api.useQuery('get', CONNECTIONS, undefined, { enabled: configured });
  const envelopes = $api.useQuery('get', ENVELOPES);
  const readable = useBankAccounts();

  useEffect(() => {
    // The id keeps it one toast when the effect runs twice (React's strict mode).
    if (arrival.connected) toast.success(t('banks.connected'), { id: 'banks-connected' });
    if (arrival.connected || arrival.error) void navigate({ to: '/settings/banks', search: {}, replace: true });
  }, [arrival, navigate, t]);
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
  const unsynced = connections.data || !configured ? (readable ?? []).filter((a) => !a.synced && !listed.some((l) => l.id === a.id)) : [];
  const all = [...listed, ...unsynced];
  const edit = (account: BankAccount) => setEditing(account);
  const announce = (message: string) => { setEditing(null); setConnecting(false); toast.success(message); };
  const connect = <Button type="button" onClick={() => setConnecting(true)}>{t('banks.connect')}</Button>;

  return (
    <div className="space-y-6">
      {arrival.error ? <p role="alert" className="text-sm text-destructive">{bankErrorText(t, arrival.error, t('banks.error.generic'))}</p> : null}
      {configured ? null : <p>{t('banks.not_configured')}</p>}
      {connections.error ? <ProblemMessage problem={present(toProblem(connections.error))} /> : null}
      {configured && !connections.data && !connections.error ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('banks.loading')}</span></Skeleton> : null}
      {connections.data?.length === 0 ? <EmptyState module="/settings/appearance" title={t('banks.empty_title')} body={t('banks.empty_body')} action={connect} /> : null}
      {connections.data && connections.data.length > 0 ? (
        <>
          <div>{connect}</div>
          {connections.data.map((c) => (
            <ConnectionSection key={c.id} connection={c} all={all} envelopes={envelopes.data?.items ?? []} onEdit={edit} />
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

function ConnectionSection({ connection, all, envelopes, onEdit }: {
  connection: Connection; all: BankAccount[]; envelopes: EnvelopeSummary[]; onEdit: (account: BankAccount) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { goTo } = useSession();
  const queryClient = useQueryClient();
  const bank = connection.aspspName;
  const path = { params: { path: { id: connection.id } } };
  const sync = useSync();
  const renew = $api.useMutation('post', RENEW, { onSuccess: (started) => goTo(started.redirectUrl) });
  const remove = $api.useMutation('delete', CONNECTION);
  const failed = [sync, renew].find((m) => m.error && toProblem(m.error).code !== 'banking.sync_running')?.error;

  return (
    <Section title={bank}>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
        <span className="font-medium text-foreground">{t(`banks.status.${connection.status}`, { defaultValue: connection.status })}</span>
        {connection.validUntil ? <span>{t('banks.expires', { date: day.format(new Date(connection.validUntil)) })}</span> : null}
        <span>{connection.lastSyncedAt ? t('banks.last_sync', { date: formatInstant(connection.lastSyncedAt) }) : t('banks.never_synced')}</span>
        <RowActions menuId={`bank-${connection.id}`} menuLabel={t('banks.actions_of', { bank })}
          items={[
            { label: t('banks.sync'), onSelect: () => sync.mutate({ body: { connectionId: connection.id } }) },
            { label: t('banks.renew'), onSelect: () => renew.mutate(path) },
          ]}
          confirmation={t('banks.delete_confirm', { bank })} remove={remove}
          onConfirm={() => remove.mutate(path, { onSuccess: () => { toast.success(t('banks.deleted', { bank })); void refresh(queryClient); } })}
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

const invalidateRules = (queryClient: QueryClient) => queryClient.invalidateQueries({ queryKey: ['get', RULES] });

/** « Règles mémorisées » (5C §6.3 step 3): the rules the person's own links kept; deleting one unlinks nothing already linked. */
function Rules() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const rules = $api.useQuery('get', RULES);
  // A real-estate target is a loan or a property: the two lists (unpaged, ended loans included) tell which.
  const estate = { enabled: rules.data?.some((r) => r.module === 'realestate') === true };
  const loans = $api.useQuery('get', LOANS, { params: { query: { ended: true } } }, estate);
  const properties = $api.useQuery('get', PROPERTIES, {}, estate);
  const target = (r: BankRule) => {
    // Null when the person no longer reads the target: the rule links nothing until they do again.
    if (!r.itemName) return <span className="text-muted-foreground">{t('banks.rules.hidden')}</span>;
    const listed = (list: { items: { id: string }[] } | undefined) => list?.items.some((i) => i.id === r.itemId) === true;
    // Found in neither list (archived, module off, still loading): the name alone, never a wrong page.
    const to = r.module === 'subscriptions' ? '/subscriptions/$id' : r.module === 'childcare' ? '/childcare/$id'
      : listed(loans.data) ? '/loans/$id' : listed(properties.data) ? '/properties/$id' : null;
    return to ? <Link to={to} params={{ id: r.itemId }} className="underline">{r.itemName}</Link> : r.itemName;
  };
  const lastUsed = (r: BankRule) => (r.lastUsedAt ? formatInstantDay(r.lastUsedAt) : t('banks.rules.never'));
  const actions = (r: BankRule, menuId: string) => (
    <RuleActions rule={r} menuId={menuId} />
  );

  return (
    <Section title={t('banks.rules.title')}>
      <p className="text-sm text-muted-foreground">{t('banks.rules.hint')}</p>
      {rules.error ? <ProblemMessage problem={present(toProblem(rules.error))} /> : null}
      {rules.data?.length === 0 ? <p>{t('banks.rules.empty')}</p> : null}
      {rules.data && rules.data.length > 0 ? (
        <SortableTable<BankRule> caption={t('banks.rules.title')} rows={rules.data} rowKey={(r) => r.id} initialSort={{ id: 'stem', desc: false }}
          columns={[
            { id: 'stem', header: t('banks.rules.stem'), sort: (r) => r.labelStem, cell: (r) => <span className="whitespace-normal">{r.labelStem}</span> },
            { id: 'account', header: t('banks.account'), sort: (r) => r.accountName ?? '', cell: (r) => r.accountName ?? '—' },
            { id: 'target', header: t('banks.rules.target'), sort: (r) => r.itemName ?? '', cell: target },
            { id: 'used', header: t('banks.rules.last_used'), sort: (r) => r.lastUsedAt ?? '', cell: lastUsed },
            { id: 'actions', header: t('banks.actions'), align: 'end', cell: (r) => actions(r, `rule-${r.id}`) },
          ]}
          mobile={(r) => (
            <div className="space-y-1">
              <div className="flex items-start justify-between gap-2"><span className="whitespace-normal">{r.labelStem}</span>{actions(r, `rule-${r.id}-mobile`)}</div>
              <p className="text-sm text-muted-foreground">{r.accountName ?? '—'} · {target(r)} · {lastUsed(r)}</p>
            </div>
          )} />
      ) : null}
    </Section>
  );
}

function RuleActions({ rule, menuId }: { rule: BankRule; menuId: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', RULE, { onSuccess: () => { toast.success(t('banks.rules.deleted')); void invalidateRules(queryClient); } });
  return (
    <RowActions menuId={menuId} menuLabel={t('banks.rules.actions_of', { stem: rule.labelStem })} items={[]}
      confirmation={t('banks.rules.delete_confirm', { stem: rule.labelStem })} remove={remove}
      onConfirm={() => remove.mutate({ params: { path: { id: rule.id } } })}
      present={(_, error) => present(toProblem(error))} invalidate={invalidateRules} />
  );
}
