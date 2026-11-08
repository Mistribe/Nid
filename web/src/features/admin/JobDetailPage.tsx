import { useParams } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AdminSkeleton } from './AdminFrame';
import { formatDuration } from './format';
import { formatInstant } from '@/lib/format';
import { JobOutcome } from './JobOutcome';
import { LastRun } from './JobsPage';
import { jobDependsOn, jobDescription, jobName, scheduleInWords } from './jobs';
import '@/locales/admin';

type JobRunView = components['schemas']['JobRunView'];

/** screens.md §12, a job: what it does, what it depends on, its schedule, then its last 100 runs. */
export function JobDetailPage() {
  const { t } = useTranslation();
  const { key } = useParams({ from: '/admin/jobs/$key' });
  const { $api } = useApi();
  const job = $api.useQuery('get', '/api/v1/admin/jobs/{key}', { params: { path: { key } } });
  const { name, known } = jobName(t, key);
  const crumbs = [{ label: t('nav.admin'), to: '/admin' as const }, { label: t('admin.jobs.title'), to: '/admin/jobs' as const }];

  if (job.error) return <div className="space-y-6"><PageHeader crumbs={crumbs} title={name} /><ProblemMessage problem={present(toProblem(job.error))} /></div>;
  if (!job.data) return <div className="space-y-6"><PageHeader crumbs={crumbs} title={name} /><AdminSkeleton /></div>;
  const data = job.data;
  const description = jobDescription(t, key);
  const dependsOn = jobDependsOn(t, key);

  return (
    <div className="space-y-6">
      <PageHeader crumbs={crumbs} crumbLabel={name} title={<span className={known ? undefined : 'font-mono'}>{name}</span>}
        meta={known ? <span className="font-mono text-sm text-muted-foreground">{t('admin.jobs.key_label', { key })}</span> : null}>
        <StatLine items={[
          { label: t('admin.jobs.schedule_col'), value: scheduleInWords(t, data.cron) },
          { label: t('admin.jobs.next'), value: data.nextRunAt ? formatInstant(data.nextRunAt) : t('admin.jobs.on_demand') },
          { label: t('admin.jobs.last'), value: <LastRun run={data.runs[0]} /> },
        ]} />
      </PageHeader>
      {description || dependsOn ? (
        <Section title={t('admin.jobs.about')}>
          {description ? <p>{description}</p> : null}
          {dependsOn ? <p><span className="text-muted-foreground">{t('admin.jobs.depends_on')} : </span>{dependsOn}</p> : null}
        </Section>
      ) : null}
      <Section title={t('admin.jobs.history')}>
        {data.runs.length === 0 ? <p className="text-muted-foreground">{t('admin.jobs.no_runs')}</p> : (
          <SortableTable<JobRunView> caption={t('admin.jobs.history')} rows={data.runs} rowKey={(r) => r.id}
            rowLink={(r) => ({ to: '/admin/jobs/$key/runs/$runId', params: { key, runId: r.id } })}
            columns={[
              { id: 'started', header: t('admin.jobs.started'), sort: (r) => r.startedAt, cell: (r) => formatInstant(r.startedAt) },
              { id: 'trigger', header: t('admin.jobs.trigger'), cell: (r) => t(`admin.jobs.triggers.${r.trigger}`, { defaultValue: r.trigger }) },
              { id: 'duration', header: t('admin.jobs.duration'), align: 'end', sort: (r) => r.durationMs ?? Number.NaN, cell: (r) => (r.durationMs != null ? formatDuration(r.durationMs) : '') },
              { id: 'outcome', header: t('admin.jobs.outcome'), sort: (r) => r.outcome, cell: (r) => <JobOutcome outcome={r.outcome} /> },
              { id: 'summary', header: t('admin.jobs.summary'), wrap: true, cell: (r) => <span className="font-mono text-xs break-all">{r.summary ?? ''}</span> },
            ]}
            mobile={(r) => (
              <div className="space-y-1">
                <div className="flex items-center justify-between gap-2"><span>{formatInstant(r.startedAt)}</span><JobOutcome outcome={r.outcome} /></div>
                {r.summary ? <div className="font-mono text-xs break-all text-muted-foreground">{r.summary}</div> : null}
              </div>
            )} />
        )}
      </Section>
    </div>
  );
}
