import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, aiReason, JOB_STATUSES, JOBS, jobType, type ModelJob } from '@/api/ai';
import { DOCUMENT } from '@/api/documents';
import { EmptyState } from '@/components/EmptyState';
import { ModalDialog } from '@/components/ModalDialog';
import { Button } from '@/components/ui/button';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SelectField } from '@/components/form/SelectField';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { formatInstant } from '@/lib/format';
import { AdminFrame } from '../AdminFrame';
import '@/locales/ai';

/**
 * Réglages › IA › Tâches (#476): every model job, newest first, where it stands and why it waits, never its content. Type, status
 * and page live in the URL and the server applies them. #602: the document a job is about (named and linked when the admin
 * reads it) and, in a dialog, each time it was sent to a worker.
 */
export function AiJobsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const navigate = useNavigate();
  const search: { status?: string; type?: string } = useSearch({ strict: false });
  // The root route keeps the raw address: a status the server would refuse reads as « all ».
  const status = search.status !== undefined && (JOB_STATUSES as readonly string[]).includes(search.status) ? search.status : '';
  const type = search.type ?? '';
  const [page, toPage] = useListPage();
  // A new filter starts again at the first page.
  const filter = (key: 'status' | 'type', value: string) =>
    void navigate({ to: '.', replace: true, search: (prev) => ({ ...prev, [key]: value || undefined, page: undefined }) });
  // Refreshed every 10 s while the page is in view: a batch of OCR moves on its own.
  const jobs = $api.useQuery('get', JOBS, { params: { query: { ...(status ? { status } : {}), ...(type ? { type } : {}), page } } },
    { refetchInterval: 10_000 });
  useStepBack(page, jobs.data, toPage);

  const title = t('ai.jobs.title');
  const statusLabel = (s: string) => t(`ai.jobs.status_${s}` as 'ai.jobs.status_queued', { defaultValue: s });
  const reason = (job: ModelJob) => aiReason(t, job.waitingReason ?? job.error);
  const types = jobs.data?.types ?? (type ? [type] : []);
  const filtered = status !== '' || type !== '';
  const [shown, setShown] = useState<ModelJob | null>(null);
  const file = (job: ModelJob) => !job.file ? '—' : job.file.reference && job.file.module === 'documents'
    ? <JobDocument id={job.file.reference} /> : t('ai.jobs.document');
  const dispatches = (job: ModelJob) => job.dispatches.length === 0 ? '—' : (
    <Button type="button" variant="link" size="sm" className="h-auto p-0" onClick={() => setShown(job)}>
      {t('ai.jobs.dispatches_count', { count: job.dispatches.length })}
    </Button>
  );

  return (
    <AdminFrame title={title}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('ai.jobs.tip')}</p>
      {jobs.data ? (
        <StatLine items={(['queued', 'running', 'succeeded', 'failed', 'dead'] as const).map((s) => ({
          label: t(`ai.jobs.count_${s}`), value: jobs.data.counts[s],
        }))} />
      ) : null}
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('ai.jobs.type')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-56 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={type} onValueChange={(v) => filter('type', v)}
                options={[{ value: '', label: t('ai.jobs.all_types') }, ...types.map((name) => ({ value: name, label: jobType(t, name) }))]} />
            </div>
          )}
        </FilterField>
        <FilterField label={t('ai.jobs.status')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-48 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={status} onValueChange={(v) => filter('status', v)}
                options={[{ value: '', label: t('ai.jobs.all_statuses') }, ...JOB_STATUSES.map((s) => ({ value: s, label: statusLabel(s) }))]} />
            </div>
          )}
        </FilterField>
      </FilterRow>
      {jobs.error ? <ProblemMessage problem={aiProblem(t, jobs.error)} /> : !jobs.data ? (
        <Loading label={t('ai.loading')}><TableSkeleton columns={9} /></Loading>
      ) : jobs.data.total === 0 ? (
        filtered ? <p className="text-sm text-muted-foreground">{t('ai.jobs.none_match')}</p>
          : <EmptyState module="/settings/appearance" title={t('ai.jobs.empty')} body={t('ai.jobs.empty_body')} />
      ) : jobs.data.items.length === 0 ? <Loading label={t('ai.loading')}><TableSkeleton columns={9} /></Loading> : (
        <>
          <SortableTable<ModelJob>
            caption={title}
            rows={jobs.data.items}
            rowKey={(job) => job.id}
            columns={[
              { id: 'type', header: t('ai.jobs.type'), cell: (job) => jobType(t, job.type) },
              { id: 'document', header: t('ai.jobs.document'), wrap: true, cell: file },
              { id: 'status', header: t('ai.jobs.status'), cell: (job) => statusLabel(job.status) },
              { id: 'reason', header: t('ai.jobs.reason'), wrap: true, cell: (job) => reason(job) ?? '—' },
              { id: 'attempts', header: t('ai.jobs.attempts'), align: 'end', cell: (job) => job.attempts },
              { id: 'worker', header: t('ai.jobs.worker'), cell: (job) => job.workerName ?? '—' },
              { id: 'created', header: t('ai.jobs.created'), cell: (job) => formatInstant(job.createdAt) },
              { id: 'finished', header: t('ai.jobs.finished'), cell: (job) => (job.finishedAt ? formatInstant(job.finishedAt) : '—') },
              { id: 'dispatches', header: t('ai.jobs.dispatches'), cell: dispatches },
            ]}
            mobile={(job) => (
              <div className="space-y-1">
                <div className="flex items-center justify-between gap-2"><span>{jobType(t, job.type)}</span><span className="text-sm text-muted-foreground">{formatInstant(job.createdAt)}</span></div>
                <p className="text-sm text-muted-foreground">{[statusLabel(job.status), job.workerName].filter(Boolean).join(' · ')}</p>
                {job.file ? <p className="text-sm">{file(job)}</p> : null}
                {reason(job) ? <p className="text-sm">{reason(job)}</p> : null}
                {job.dispatches.length > 0 ? <p className="text-sm">{dispatches(job)}</p> : null}
              </div>
            )}
          />
          <Pager page={page} total={jobs.data.total} onPage={toPage} />
          {shown ? (
            <ModalDialog title={t('ai.jobs.dispatches_title', { type: jobType(t, shown.type) })} onClose={() => setShown(null)}>
              {() => (
                <>
                  <ol className="divide-y text-sm">
                    {shown.dispatches.map((d) => (
                      <li key={d.sentAt} className="space-y-1 py-2">
                        <p className="flex justify-between gap-2"><span className="font-medium">{d.workerName ?? t('ai.jobs.worker_gone')}</span><span className="text-muted-foreground">{formatInstant(d.sentAt)}</span></p>
                        <p className="text-muted-foreground">
                          {d.outcome === null || d.outcome === undefined ? t('ai.jobs.dispatch_running')
                            : d.outcome === 'succeeded' ? t('ai.jobs.dispatch_succeeded') : aiReason(t, d.outcome)}
                          {d.finishedAt ? ` · ${formatInstant(d.finishedAt)}` : ''}
                        </p>
                      </li>
                    ))}
                  </ol>
                  <div className="flex justify-end"><Button type="button" variant="outline" onClick={() => setShown(null)}>{t('common.close')}</Button></div>
                </>
              )}
            </ModalDialog>
          ) : null}
        </>
      )}
    </AdminFrame>
  );
}

/**
 * #602: a job's document, which the jobs page names only through Documents, under the admin's own rights (an admin-only
 * answer carries no item's name); « Document » until its title comes, or when it cannot be read.
 */
function JobDocument({ id }: { id: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const document = $api.useQuery('get', DOCUMENT, { params: { path: { id } } }, { retry: false });
  return (
    <Link to="/documents/$id" params={{ id }} className="underline-offset-4 hover:underline">
      {document.data?.document.title ?? t('ai.jobs.document')}
    </Link>
  );
}
