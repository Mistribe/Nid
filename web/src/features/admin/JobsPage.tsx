import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { SortableTable } from '@/components/page/SortableTable';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AdminFrame } from './AdminFrame';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { formatDuration } from './format';
import { formatInstant } from '@/lib/format';
import { JobOutcome } from './JobOutcome';
import { jobDescription, jobName, scheduleInWords } from './jobs';
import '@/locales/admin';

type JobView = components['schemas']['JobView'];
type JobRunView = components['schemas']['JobRunView'];

/** screens.md §12, Tâches: read only, « est-ce que mes tâches tournent bien, et sinon pourquoi ? ». */
export function JobsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const jobs = $api.useQuery('get', '/api/v1/admin/jobs');

  const title = t('admin.jobs.title');
  if (jobs.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(jobs.error))} /></AdminFrame>;
  if (!jobs.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><TableSkeleton columns={4} /></Loading></AdminFrame>;
  if (jobs.data.length === 0)
    return <AdminFrame title={title}><EmptyState module="/admin" title={t('admin.jobs.empty_title')} body={t('admin.jobs.empty_body')} /></AdminFrame>;

  return (
    <AdminFrame title={title}>
      <SortableTable<JobView> caption={title} rows={jobs.data} rowKey={(j) => j.key}
        rowLink={(j) => ({ to: '/admin/jobs/$key', params: { key: j.key } })}
        columns={[
          { id: 'job', header: t('admin.jobs.job'), wrap: true, sort: (j) => jobName(t, j.key).name, cell: (j) => <JobTitle jobKey={j.key} /> },
          { id: 'schedule', header: t('admin.jobs.schedule_col'), cell: (j) => scheduleInWords(t, j.cron) },
          { id: 'last', header: t('admin.jobs.last'), sort: (j) => j.lastRun?.startedAt ?? '', cell: (j) => <LastRun run={j.lastRun} /> },
          { id: 'next', header: t('admin.jobs.next'), sort: (j) => j.nextRunAt ?? '', cell: (j) => (j.nextRunAt ? formatInstant(j.nextRunAt) : t('admin.jobs.on_demand')) },
        ]}
        mobile={(j) => (
          <div className="space-y-1">
            <JobTitle jobKey={j.key} />
            <div className="text-sm text-muted-foreground">{scheduleInWords(t, j.cron)}</div>
            <LastRun run={j.lastRun} />
          </div>
        )} />
    </AdminFrame>
  );
}

/** The French name with its one-line description; the raw key only for a job the app does not know. */
function JobTitle({ jobKey }: { jobKey: string }) {
  const { t } = useTranslation();
  const { name, known } = jobName(t, jobKey);
  const description = jobDescription(t, jobKey);
  return (
    <span className="inline-flex flex-col">
      <span className={known ? 'font-medium' : 'font-mono'}>{name}</span>
      {description ? <span className="text-xs text-muted-foreground">{description}</span> : null}
    </span>
  );
}

/** Date, outcome and duration of a run; « jamais » before the first one. */
export function LastRun({ run }: { run: JobRunView | null | undefined }) {
  const { t } = useTranslation();
  if (!run) return <span className="text-muted-foreground">{t('admin.jobs.never')}</span>;
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <span>{formatInstant(run.startedAt)}</span>
      <JobOutcome outcome={run.outcome} />
      {run.durationMs != null ? <span className="text-xs text-muted-foreground tabular-nums">{formatDuration(run.durationMs)}</span> : null}
    </span>
  );
}
