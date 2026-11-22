import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import {
  AI_SETTINGS, aiProblem, NID_KEY_ROTATE, REMOTE_WORKERS, WORKER, WORKER_ACTIONS, WORKER_SAFE, WORKER_TEST, WORKERS,
  type Worker,
} from '@/api/ai';
import { useSession } from '@/app/Providers';
import { ConfirmAction, ConfirmFromMenu } from '@/components/ConfirmAction';
import { EmptyState } from '@/components/EmptyState';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { InfoTip } from '@/components/InfoTip';
import { ModalDialog } from '@/components/ModalDialog';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { SwitchField } from '@/components/page/SwitchField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { formatInstant } from '@/lib/format';
import { SettingsFrame } from '../SettingsFrame';
import '@/locales/ai';
import { RegisterWorkerDialog } from './RegisterWorkerDialog';

const WORKER_CLI = 'docker compose exec worker dotnet Nid.Ai.Worker.dll';
/** What an admin runs on each worker to adopt Nid's new key: the fingerprint as one argument, without its spaces. */
const confirmCommand = (fingerprint: string) => `${WORKER_CLI} confirm-rotation ${fingerprint.replaceAll(' ', '')}`;

/** A refused action in one line: the server's `ai.*` code in words, else the kind of failure. */
const failure = (t: TFunction, error: unknown) => {
  const problem = aiProblem(t, error);
  return problem.detail ?? t(`problem.${problem.kind}`);
};

/**
 * Réglages › IA (#476, docs/ai/README.md §12): every adult sees the workers, opens remote workers to the household's data and
 * marks one `safe`; an admin adds, pairs, tests and manages them and rotates Nid's key. The server stays the authority.
 */
export function AiSettingsPage() {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const admin = capabilities.admin;
  const [adding, setAdding] = useState<{ pending?: Worker } | null>(null);
  const announce = (message: string) => { setAdding(null); toast.success(message); };

  return (
    <SettingsFrame title={t('ai.title')} actions={admin ? <Button type="button" onClick={() => setAdding({})}>{t('ai.workers.add')}</Button> : null}>
      <DataSection />
      <WorkersSection admin={admin} onPair={(worker) => setAdding({ pending: worker })} onAdd={() => setAdding({})} />
      {admin ? <NidKeySection /> : null}
      {adding ? <RegisterWorkerDialog pending={adding.pending} onClose={() => setAdding(null)} onDone={announce} /> : null}
    </SettingsFrame>
  );
}

/** « Données »: the household's opt-in for remote workers, any adult; every adult hears of a change. */
function DataSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const settings = $api.useQuery('get', AI_SETTINGS);
  const change = $api.useMutation('put', REMOTE_WORKERS, {
    onSuccess: (view) => {
      toast.success(t(view.remoteWorkersAllowed ? 'ai.data.allowed' : 'ai.data.refused'));
      void queryClient.invalidateQueries({ queryKey: ['get', AI_SETTINGS] });
    },
  });
  const error = settings.error ?? change.error;
  return (
    <Section title={t('ai.data.title')}>
      {error ? <ProblemMessage problem={aiProblem(t, error)} /> : null}
      {settings.data ? (
        <SwitchField label={t('ai.data.remote')} hint={t('ai.data.remote_hint')} checked={settings.data.remoteWorkersAllowed}
          tip={<InfoTip label={t('ai.data.remote')}>{t('ai.data.remote_tip')}</InfoTip>}
          onCheckedChange={(allowed) => change.mutate({ body: { allowed } })} />
      ) : settings.error ? null : <Skeleton aria-busy="true" className="h-12"><span className="sr-only">{t('ai.loading')}</span></Skeleton>}
    </Section>
  );
}

function WorkersSection({ admin, onPair, onAdd }: { admin: boolean; onPair: (worker: Worker) => void; onAdd: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const workers = $api.useQuery('get', WORKERS);
  const jobs = <Link to="/settings/ai/jobs" className="text-sm font-medium underline underline-offset-4">{t('ai.jobs.link')}</Link>;

  const placement = (w: Worker) => [t(w.placement === 'remote' ? 'ai.workers.placement_remote' : 'ai.workers.placement_local'), w.proxyDeclared ? t('ai.workers.proxy') : null]
    .filter(Boolean).join(', ');
  const status = (w: Worker) => t(`ai.workers.status_${w.status}` as 'ai.workers.status_active', { defaultValue: w.status });
  const seen = (w: Worker) => (w.lastSeenAt ? t('ai.workers.seen', { when: formatInstant(w.lastSeenAt) }) : t('ai.workers.never_seen'));
  const name = (w: Worker) => (
    <span className="flex flex-wrap items-center gap-1.5">
      {w.name}
      {w.nidKeyRotationOverdue ? <Badge variant="outline">{t('ai.workers.overdue')}</Badge> : null}
    </span>
  );
  const actions = (w: Worker) => <WorkerActions worker={w} admin={admin} onPair={onPair} />;

  return (
    <Section title={t('ai.workers.title')} actions={jobs} tip={(
      <InfoTip label={t('ai.workers.title')}>
        <span className="block">{t('ai.worker_tip')}</span>
        <span className="mt-2 block">{t('ai.workers.safe_tip')}</span>
        <span className="mt-2 block">{t('ai.workers.load_tip')}</span>
      </InfoTip>
    )}>
      {workers.error ? <ProblemMessage problem={aiProblem(t, workers.error)} /> : null}
      {!workers.data && !workers.error ? <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('ai.loading')}</span></Skeleton> : null}
      {workers.data?.workers.length === 0 ? (
        <EmptyState module="/settings/appearance" level={3} title={t('ai.workers.empty')} body={t('ai.workers.empty_body')}
          action={admin ? <Button type="button" onClick={onAdd}>{t('ai.workers.add')}</Button> : undefined} />
      ) : null}
      {workers.data && workers.data.workers.length > 0 ? (
        <SortableTable<Worker>
          caption={t('ai.workers.title')}
          rows={workers.data.workers}
          rowKey={(w) => w.id}
          columns={[
            { id: 'name', header: t('ai.workers.name'), cell: name, sort: (w) => w.name },
            { id: 'placement', header: t('ai.workers.placement'), cell: placement },
            { id: 'safe', header: t('ai.workers.safe'), cell: (w) => t(w.safe ? 'ai.workers.safe_yes' : 'ai.workers.safe_no') },
            { id: 'status', header: t('ai.workers.status'), cell: (w) => <span>{status(w)}<span className="block text-xs text-muted-foreground">{seen(w)}</span></span> },
            { id: 'version', header: t('ai.workers.version'), cell: (w) => w.softwareVersion ?? '—' },
            { id: 'load', header: t('ai.workers.load'), align: 'end', cell: (w) => w.load },
            { id: 'actions', header: t('page.more_actions'), align: 'end', cell: actions },
          ]}
          mobile={(w) => (
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0 space-y-0.5">
                {name(w)}
                <p className="text-sm text-muted-foreground">{[placement(w), w.safe ? t('ai.workers.safe') : null, status(w)].filter(Boolean).join(' · ')}</p>
                <p className="text-xs text-muted-foreground">{seen(w)}</p>
              </div>
              {actions(w)}
            </div>
          )}
        />
      ) : null}
    </Section>
  );
}

type Asking = 'disable' | 'rotate' | 'revoke' | 'rename';

/** A worker's `⋯`: « sûr » for any adult; pairing, test and management for an admin, each answered by a toast. */
function WorkerActions({ worker, admin, onPair }: { worker: Worker; admin: boolean; onPair: (worker: Worker) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const menu = useRef<HTMLButtonElement | null>(null);
  const [asking, setAsking] = useState<Asking | null>(null);
  const path = { params: { path: { id: worker.id } } };
  const done = (message: string) => ({
    onSuccess: () => { toast.success(message); void queryClient.invalidateQueries({ queryKey: ['get', WORKERS] }); },
    onError: (error: unknown) => { toast.error(failure(t, error)); },
  });
  const named = { name: worker.name };
  const refresh = $api.useMutation('post', WORKER_ACTIONS.refresh, done(t('ai.workers.refreshed', named)));
  const disable = $api.useMutation('post', WORKER_ACTIONS.disable, done(t('ai.workers.disabled', named)));
  const enable = $api.useMutation('post', WORKER_ACTIONS.enable, done(t('ai.workers.enabled', named)));
  const rotate = $api.useMutation('post', WORKER_ACTIONS.rotate, done(t('ai.workers.rotated', named)));
  const revoke = $api.useMutation('post', WORKER_ACTIONS.revoke, done(t('ai.workers.revoked', named)));
  const safe = $api.useMutation('put', WORKER_SAFE, done(t(worker.safe ? 'ai.workers.safe_off' : 'ai.workers.safe_on', named)));
  const test = $api.useMutation('post', WORKER_TEST, {
    onSuccess: (result) => toast.success(t('ai.workers.tested', { count: result.frames }), {
      action: { label: t('ai.workers.see_jobs'), onClick: () => void navigate({ to: '/settings/ai/jobs' }) },
    }),
    onError: (error) => { toast.error(failure(t, error)); },
  });

  const live = worker.status !== 'revoked';
  const items = [
    ...(admin && worker.status === 'pending' ? [{ label: t('ai.workers.pair'), onSelect: () => onPair(worker) }] : []),
    ...(admin && worker.status === 'active' ? [
      { label: t('ai.workers.test'), onSelect: () => test.mutate(path) },
      { label: t('ai.workers.refresh'), onSelect: () => refresh.mutate(path) },
    ] : []),
    ...(live ? [{ label: t(worker.safe ? 'ai.workers.unmark_safe' : 'ai.workers.mark_safe'), onSelect: () => safe.mutate({ ...path, body: { safe: !worker.safe } }) }] : []),
    ...(admin && live ? [{ label: t('ai.workers.rename'), onSelect: () => setAsking('rename') }] : []),
    ...(admin && worker.status === 'active' ? [
      { label: t('ai.workers.disable'), onSelect: () => setAsking('disable') },
      { label: t('ai.workers.rotate'), onSelect: () => setAsking('rotate') },
    ] : []),
    ...(admin && worker.status === 'disabled' ? [{ label: t('ai.workers.enable'), onSelect: () => enable.mutate(path) }] : []),
    ...(admin && live ? [{ label: t('ai.workers.revoke'), destructive: true, onSelect: () => setAsking('revoke') }] : []),
  ];
  if (items.length === 0) return null;

  const confirm = asking === 'disable' ? { label: t('ai.workers.disable'), confirmation: t('ai.workers.disable_confirm', named), run: () => disable.mutate(path), pending: disable.isPending }
    : asking === 'rotate' ? { label: t('ai.workers.rotate'), confirmation: t('ai.workers.rotate_confirm'), run: () => rotate.mutate(path), pending: rotate.isPending }
    : asking === 'revoke' ? { label: t('ai.workers.revoke'), confirmation: t('ai.workers.revoke_confirm', named), run: () => revoke.mutate(path), pending: revoke.isPending }
    : null;

  return (
    <>
      <RowMenu label={t('ai.workers.actions', named)} triggerRef={(node) => { menu.current = node; }} items={items} />
      {confirm ? (
        <ConfirmFromMenu label={confirm.label} confirmation={confirm.confirmation} pending={confirm.pending} returnFocus={menu}
          onClose={() => setAsking(null)} onConfirm={confirm.run} />
      ) : null}
      {asking === 'rename' ? <RenameDialog worker={worker} onClose={() => setAsking(null)} /> : null}
    </>
  );
}

function RenameDialog({ worker, onClose }: { worker: Worker; onClose: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [name, setName] = useState(worker.name);
  const rename = $api.useMutation('patch', WORKER);
  return (
    <FormDialog title={t('ai.workers.rename_title', { name: worker.name })} onClose={onClose} onDone={(message) => { onClose(); toast.success(message); }}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          rename.mutate({ params: { path: { id: worker.id } }, body: { name: name.trim() } }, {
            onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['get', WORKERS] }); done(t('ai.workers.renamed')); },
          });
        }}>
          {rename.error ? <ProblemMessage problem={aiProblem(t, rename.error)} /> : null}
          <Field label={t('ai.workers.name')}>{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} maxLength={60} />}</Field>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={rename.isPending}>{t('ai.workers.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}

/** « Clé de Nid » (admin): its rotation, then the new fingerprint, the command each worker needs and the workers not told. */
function NidKeySection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const workers = $api.useQuery('get', WORKERS);
  const rotate = $api.useMutation('post', NID_KEY_ROTATE, {
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['get', WORKERS] }),
  });
  const result = rotate.data;
  const nameOf = (id: string) => workers.data?.workers.find((w) => w.id === id)?.name ?? id;
  return (
    <Section title={t('ai.key.title')} tip={<InfoTip label={t('ai.key.title')}>{t('ai.key.tip')}</InfoTip>}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('ai.key.body')}</p>
      {rotate.error ? <ProblemMessage problem={aiProblem(t, rotate.error)} /> : null}
      <ConfirmAction label={t('ai.key.rotate')} confirmation={t('ai.key.confirm')} pending={rotate.isPending} onConfirm={() => rotate.mutate({})} />
      {result ? (
        <ModalDialog title={t('ai.key.done_title')} onClose={() => rotate.reset()}>
          {() => (
            <div className="space-y-3">
              <p className="text-sm">{t('ai.key.fingerprint')}</p>
              <p className="font-mono text-sm break-all">{result.fingerprint}</p>
              <p className="text-sm">{t('ai.key.command')}</p>
              <pre className="rounded-md bg-muted px-3 py-2 text-xs break-all whitespace-pre-wrap">{confirmCommand(result.fingerprint)}</pre>
              {result.unreached.length > 0 ? (
                <p role="status" className="rounded-md bg-status-warning/10 px-3 py-2 text-sm text-status-warning-text">
                  {t('ai.key.unreached', { names: result.unreached.map(nameOf).join(', ') })}
                </p>
              ) : null}
              <div className="flex justify-end"><Button type="button" onClick={() => rotate.reset()}>{t('ai.key.close')}</Button></div>
            </div>
          )}
        </ModalDialog>
      ) : null}
    </Section>
  );
}
