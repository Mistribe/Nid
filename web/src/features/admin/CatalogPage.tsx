import { ChevronRightIcon, CircleCheck, CircleX, LoaderCircle } from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { EmptyState } from '@/components/EmptyState';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AdminFrame } from './AdminFrame';
import { Section } from '@/components/page/Section';
import { Loading, SectionSkeleton } from '@/components/page/skeletons';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { ConfirmAction } from '@/components/ConfirmAction';
import { formatInstant } from '@/lib/format';
import '@/locales/admin';

type Run = components['schemas']['CatalogSyncRunView'];
type Report = components['schemas']['CatalogReport'];
type Icons = components['schemas']['IconReport'];

/** How often the page refreshes its runs while a sync is under way (design §6). */
export const REFRESH_MS = 3_000;

/**
 * How long, after a 202, the page waits for the job's run to appear before it stops being busy and shows the latest
 * state as it is (final review H3): a job that never ran must not leave the page polling for ever.
 */
export const REQUEST_WAIT_MS = 30_000;

const RUNS = '/api/v1/subscriptions/catalog/runs';


/**
 * A sync is under way while the latest run is running, and, after a request was accepted, until a run other than
 * the one known then shows up: the job creates its run a moment after the 202 (1C2a design §10).
 * `requestedAfter` is undefined when nothing was requested, null when no run existed yet.
 */
export function isBusy(latest: { id: string; status: string } | undefined, requestedAfter: string | null | undefined): boolean {
  if (latest?.status === 'running') return true;
  if (requestedAfter === undefined) return false;
  return latest === undefined || latest.id === requestedAfter;
}

const short = (commit: string) => commit.slice(0, 7);

type RunState = 'running' | 'succeeded' | 'failed';
const ICONS = { running: LoaderCircle, succeeded: CircleCheck, failed: CircleX } as const;

/** A run's status as an icon and a word, never colour alone (design §6). */
function RunStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  const known: RunState = status === 'running' || status === 'succeeded' ? status : 'failed';
  const Icon = ICONS[known];
  return (
    <span className="inline-flex items-center gap-1">
      <Icon aria-hidden="true" className="size-4" />
      <span>{t(`admin.catalog.status.${known}`)}</span>
    </span>
  );
}

function reasonKey(reason: string) {
  switch (reason) {
    case 'invalid_key': return 'admin.catalog.reasons.invalid_key' as const;
    case 'invalid_name': return 'admin.catalog.reasons.invalid_name' as const;
    case 'invalid_url': return 'admin.catalog.reasons.invalid_url' as const;
    case 'no_category': return 'admin.catalog.reasons.no_category' as const;
    case 'invalid_category': return 'admin.catalog.reasons.invalid_category' as const;
    case 'invalid_color': return 'admin.catalog.reasons.invalid_color' as const;
    case 'not_an_object': return 'admin.catalog.reasons.not_an_object' as const;
    case 'reserved_key': return 'admin.catalog.reasons.reserved_key' as const;
    default: return 'admin.catalog.reasons.other' as const;
  }
}

function includeReasonKey(reason: string) {
  switch (reason) {
    case 'invalid_include': return 'admin.catalog.include_reasons.invalid_include' as const;
    case 'unknown_include': return 'admin.catalog.include_reasons.unknown_include' as const;
    case 'self_include': return 'admin.catalog.include_reasons.self_include' as const;
    default: return 'admin.catalog.include_reasons.other' as const;
  }
}

function iconReasonKey(reason: string) {
  switch (reason) {
    case 'unsupported_format': return 'admin.catalog.icon_reasons.unsupported_format' as const;
    case 'too_many_pixels': return 'admin.catalog.icon_reasons.too_many_pixels' as const;
    case 'too_large': return 'admin.catalog.icon_reasons.too_large' as const;
    case 'undecodable': return 'admin.catalog.icon_reasons.undecodable' as const;
    case 'host_not_allowed': return 'admin.catalog.icon_reasons.host_not_allowed' as const;
    case 'not_https': return 'admin.catalog.icon_reasons.not_https' as const;
    case 'address_refused': return 'admin.catalog.icon_reasons.address_refused' as const;
    case 'unreachable': return 'admin.catalog.icon_reasons.unreachable' as const;
    case 'http_status': return 'admin.catalog.icon_reasons.http_status' as const;
    case 'invalid_url': return 'admin.catalog.icon_reasons.invalid_url' as const;
    case 'store_failed': return 'admin.catalog.icon_reasons.store_failed' as const;
    default: return 'admin.catalog.icon_reasons.other' as const;
  }
}

/** screens.md §12, Catalogue (design §6): the last sync, its report, the history, and a confirmed sync on demand. */
export function CatalogPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [requestedAfter, setRequestedAfter] = useState<string | null | undefined>(undefined);
  const [problem, setProblem] = useState<Presentation | null>(null);

  const [page, toPage] = useListPage();
  const runs = $api.useQuery('get', RUNS, { params: { query: { page } } }, {
    refetchInterval: (query) => (isBusy(query.state.data?.latest ?? undefined, requestedAfter) ? REFRESH_MS : false),
  });
  useStepBack(page, runs.data?.runs, toPage);
  // The card and the busy state follow the newest run, whichever page the history is on.
  const latest = runs.data?.latest ?? undefined;
  const busy = isBusy(latest, requestedAfter);

  // Once the requested run has appeared, or after REQUEST_WAIT_MS without it, only the runs say whether a sync runs.
  const waiting = requestedAfter !== undefined;
  const appeared = waiting && latest !== undefined && latest.id !== requestedAfter;
  useEffect(() => {
    if (!waiting) return undefined;
    if (appeared) {
      setRequestedAfter(undefined);
      return undefined;
    }
    const timer = setTimeout(() => setRequestedAfter(undefined), REQUEST_WAIT_MS);
    return () => clearTimeout(timer);
  }, [waiting, appeared]);

  const sync = $api.useMutation('post', '/api/v1/subscriptions/catalog/sync', {
    onSuccess: () => {
      setProblem(null);
      toast.success(t('admin.catalog.sync_requested'));
      setRequestedAfter(latest?.id ?? null);
      void runs.refetch();
    },
    onError: (error) => {
      const failure = toProblem(error);
      // Not a failure to explain: the sync the admin asked for is already under way.
      if (failure.code === 'subscriptions.catalog_sync_running') {
        setProblem(null);
        toast.info(t('admin.catalog.already_running'));
      } else {
        setProblem(present(failure));
      }
    },
  });

  return (
    <AdminFrame title={t('admin.catalog.title')}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <Section title={t('admin.catalog.sync_title')}>
        {runs.data ? (
          <p className="text-sm text-muted-foreground">{t('admin.catalog.pinned', { commit: short(runs.data.pinnedCommit) })}</p>
        ) : null}
        <ConfirmAction
          label={t('admin.catalog.sync')}
          confirmation={t('admin.catalog.sync_confirm')}
          disabled={busy}
          pending={sync.isPending}
          onConfirm={() => sync.mutate({})}
        />
      </Section>

      {runs.error ? (
        <ProblemMessage problem={present(toProblem(runs.error))} />
      ) : !runs.data ? (
        <Loading label={t('admin.loading')}><SectionSkeleton /></Loading>
      ) : latest === undefined ? (
        <EmptyState module="/admin" title={t('admin.catalog.empty_title')} body={t('admin.catalog.empty_body')} />
      ) : (
        <>
          <LastSync run={latest} />
          <History runs={runs.data.runs.items} total={runs.data.runs.total} page={page} onPage={toPage} />
        </>
      )}
    </AdminFrame>
  );
}

function LastSync({ run }: { run: Run }) {
  const { t } = useTranslation();
  return (
    <section aria-labelledby="catalog-last" className="space-y-4 rounded-lg border p-4">
      <h2 id="catalog-last" className="text-lg font-semibold">{t('admin.catalog.last')}</h2>
      <p className="flex flex-wrap items-center gap-3">
        <RunStatus status={run.status} />
        <time dateTime={run.startedAt}>{formatInstant(run.startedAt)}</time>
        <span>{t('admin.catalog.synced_commit', { commit: short(run.commit) })}</span>
      </p>
      {run.report ? <Counts report={run.report} /> : null}
      <Findings run={run} />
    </section>
  );
}

function Counts({ report }: { report: Report }) {
  const { t } = useTranslation();
  const counts = [
    ['admin.catalog.counts.added', report.providersAdded.length],
    ['admin.catalog.counts.updated', report.providersUpdated.length],
    ['admin.catalog.counts.unchanged', report.providersUnchanged.length],
    ['admin.catalog.counts.archived', report.providersArchived.length],
    ['admin.catalog.counts.adopted_replaced', report.adoptedReplaced.length],
  ] as const;
  return (
    <dl className="grid grid-cols-2 gap-3 sm:grid-cols-5">
      {counts.map(([label, value]) => (
        <div key={label}>
          <dt className="text-sm text-muted-foreground">{t(label)}</dt>
          <dd className="text-lg font-semibold tabular-nums">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** The report, one collapsible block per non-empty category (design §6), and the failure when there is one. */
function Findings({ run }: { run: Run }) {
  const { t } = useTranslation();
  const report = run.report;
  return (
    <div className="space-y-2">
      {run.failureCode ? (
        <Fold title={t('admin.catalog.report.failure')} defaultOpen>
          <p><code>{run.failureCode}</code></p>
          {run.failureMessage ? <p className="text-sm text-muted-foreground">{run.failureMessage}</p> : null}
        </Fold>
      ) : null}
      {report ? (
        <>
          <Keys title={t('admin.catalog.report.adopted_remaining', { n: report.adoptedRemaining.length })} keys={report.adoptedRemaining} />
          <Keys title={t('admin.catalog.report.labels_created', { n: report.labelsCreated.length })} keys={report.labelsCreated} />
          <Keys title={t('admin.catalog.report.conflicts', { n: report.conflicts.length })} keys={report.conflicts} />
          <Lines
            title={t('admin.catalog.report.duplicates', { n: report.duplicates.length })}
            lines={report.duplicates.map((d) => ({ id: `${d.file}:${d.key}`, text: t('admin.catalog.duplicate_entry', { file: d.file, key: d.key }) }))}
          />
          <Lines
            title={t('admin.catalog.report.refused', { n: report.refused.length })}
            lines={report.refused.map((r) => ({
              id: `${r.file}:${r.index}`,
              text: t('admin.catalog.refused_entry', { file: r.file, index: r.index, key: r.key ?? '—', reason: t(reasonKey(r.reason)) }),
            }))}
          />
          <Lines
            title={t('admin.catalog.report.includes_refused', { n: report.includesRefused.length })}
            lines={report.includesRefused.map((r, index) => ({
              id: `${r.key}:${index}`,
              text: t('admin.catalog.include_entry', { key: r.key, included: r.included ?? '—', reason: t(includeReasonKey(r.reason)) }),
            }))}
          />
          <IconFindings icons={report.icons} />
        </>
      ) : null}
    </div>
  );
}

/** 1C2c design §7: one more collapsible category, shown when an icon was fetched or refused. */
function IconFindings({ icons }: { icons: Icons }) {
  const { t } = useTranslation();
  if (icons.fetched.length === 0 && icons.refused.length === 0) return null;
  return (
    <Fold title={t('admin.catalog.report.icons', { fetched: icons.fetched.length, unchanged: icons.unchanged, refused: icons.refused.length })}>
      {icons.fetched.length > 0 ? (
        <>
          <p className="text-sm text-muted-foreground">{t('admin.catalog.icons.fetched')}</p>
          <ul className="list-inside list-disc">
            {icons.fetched.map((key) => <li key={key}><code>{key}</code></li>)}
          </ul>
        </>
      ) : null}
      {icons.refused.length > 0 ? (
        <>
          <p className="text-sm text-muted-foreground">{t('admin.catalog.icons.refused')}</p>
          <ul className="list-inside list-disc">
            {icons.refused.map((r) => (
              <li key={r.key}>{t('admin.catalog.icons.refused_entry', { key: r.key, reason: t(iconReasonKey(r.reason)) })}</li>
            ))}
          </ul>
        </>
      ) : null}
    </Fold>
  );
}

/** A report category folded under its title; closed unless `defaultOpen`. */
function Fold({ title, defaultOpen = false, children }: { title: string; defaultOpen?: boolean; children: ReactNode }) {
  return (
    <Collapsible defaultOpen={defaultOpen} className="group/fold">
      <CollapsibleTrigger className="flex items-center gap-1 rounded-sm font-medium focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none">
        <ChevronRightIcon aria-hidden="true" className="size-4 transition-transform group-data-[state=open]/fold:rotate-90" />
        {title}
      </CollapsibleTrigger>
      <CollapsibleContent className="pl-5">{children}</CollapsibleContent>
    </Collapsible>
  );
}

function Keys({ title, keys }: { title: string; keys: readonly string[] }) {
  if (keys.length === 0) return null;
  return (
    <Fold title={title}>
      <ul className="list-inside list-disc">
        {keys.map((key) => <li key={key}><code>{key}</code></li>)}
      </ul>
    </Fold>
  );
}

function Lines({ title, lines }: { title: string; lines: readonly { id: string; text: string }[] }) {
  if (lines.length === 0) return null;
  return (
    <Fold title={title}>
      <ul className="list-inside list-disc">
        {lines.map((line) => <li key={line.id}>{line.text}</li>)}
      </ul>
    </Fold>
  );
}

function History({ runs, total, page, onPage }: { runs: readonly Run[]; total: number; page: number; onPage: (page: number) => void }) {
  const { t } = useTranslation();
  return (
    <Section title={t('admin.catalog.history')}>
      <ol className="divide-y rounded-lg border">
        {runs.map((run) => (
          <li key={run.id} className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm">
            <RunStatus status={run.status} />
            <time dateTime={run.startedAt}>{formatInstant(run.startedAt)}</time>
            <code>{short(run.commit)}</code>
            {run.failureCode ? <code>{run.failureCode}</code> : null}
          </li>
        ))}
      </ol>
      <Pager page={page} total={total} onPage={onPage} />
    </Section>
  );
}
