import { useNavigate, useSearch } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { aiProblem, aiReason, JOB_STATUSES, JOBS, type ModelJob } from '@/api/ai';
import { EmptyState } from '@/components/EmptyState';
import { FilterField, FilterRow } from '@/components/page/FilterRow';
import { Pager, useListPage, useStepBack } from '@/components/page/Pager';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SelectField } from '@/components/form/SelectField';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { formatInstant } from '@/lib/format';
import { SettingsFrame } from '../SettingsFrame';
import '@/locales/ai';

/**
 * Réglages › IA › Tâches (#476): every model job, newest first, where it stands and why it waits, never its content. Type, status
 * and page live in the URL and the server applies them.
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
  const jobs = $api.useQuery('get', JOBS, { params: { query: { ...(status ? { status } : {}), ...(type ? { type } : {}), page } } });
  useStepBack(page, jobs.data, toPage);

  const title = t('ai.jobs.title');
  const statusLabel = (s: string) => t(`ai.jobs.status_${s}` as 'ai.jobs.status_queued', { defaultValue: s });
  const reason = (job: ModelJob) => aiReason(t, job.waitingReason ?? job.error);
  const types = jobs.data?.types ?? (type ? [type] : []);
  const filtered = status !== '' || type !== '';

  return (
    <SettingsFrame title={title}>
      <p className="max-w-prose text-sm text-muted-foreground">{t('ai.jobs.tip')}</p>
      <FilterRow label={t('common.filters')}>
        <FilterField label={t('ai.jobs.type')} className="w-full sm:w-auto">
          {(labelId, controlId) => (
            <div className="min-w-0 flex-1 sm:w-56 sm:flex-none">
              <SelectField id={controlId} aria-labelledby={labelId} value={type} onValueChange={(v) => filter('type', v)}
                options={[{ value: '', label: t('ai.jobs.all_types') }, ...types.map((name) => ({ value: name, label: name }))]} />
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
        <Loading label={t('ai.loading')}><TableSkeleton columns={5} /></Loading>
      ) : jobs.data.total === 0 ? (
        filtered ? <p className="text-sm text-muted-foreground">{t('ai.jobs.none_match')}</p>
          : <EmptyState module="/settings/appearance" title={t('ai.jobs.empty')} body={t('ai.jobs.empty_body')} />
      ) : jobs.data.items.length === 0 ? <Loading label={t('ai.loading')}><TableSkeleton columns={5} /></Loading> : (
        <>
          <SortableTable<ModelJob>
            caption={title}
            rows={jobs.data.items}
            rowKey={(job) => job.id}
            columns={[
              { id: 'type', header: t('ai.jobs.type'), cell: (job) => job.type },
              { id: 'status', header: t('ai.jobs.status'), cell: (job) => statusLabel(job.status) },
              { id: 'reason', header: t('ai.jobs.reason'), wrap: true, cell: (job) => reason(job) ?? '—' },
              { id: 'attempts', header: t('ai.jobs.attempts'), align: 'end', cell: (job) => job.attempts },
              { id: 'worker', header: t('ai.jobs.worker'), cell: (job) => job.workerName ?? '—' },
              { id: 'created', header: t('ai.jobs.created'), cell: (job) => formatInstant(job.createdAt) },
              { id: 'finished', header: t('ai.jobs.finished'), cell: (job) => (job.finishedAt ? formatInstant(job.finishedAt) : '—') },
            ]}
            mobile={(job) => (
              <div className="space-y-1">
                <div className="flex items-center justify-between gap-2"><span>{job.type}</span><span className="text-sm text-muted-foreground">{formatInstant(job.createdAt)}</span></div>
                <p className="text-sm text-muted-foreground">{[statusLabel(job.status), job.workerName].filter(Boolean).join(' · ')}</p>
                {reason(job) ? <p className="text-sm">{reason(job)}</p> : null}
              </div>
            )}
          />
          <Pager page={page} total={jobs.data.total} onPage={toPage} />
        </>
      )}
    </SettingsFrame>
  );
}
