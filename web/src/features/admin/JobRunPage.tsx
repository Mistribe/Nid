import { useParams } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AdminSkeleton } from './AdminFrame';
import { formatDuration, formatInstant } from './format';
import { JobOutcome } from './JobOutcome';
import { jobName } from './jobs';
import '@/locales/admin';

/** screens.md §12, one run: its figures, its error, its logs (filtered when written, authorization.md §8). */
export function JobRunPage() {
  const { t } = useTranslation();
  const { key, runId } = useParams({ from: '/admin/jobs/$key/runs/$runId' });
  const { $api } = useApi();
  const run = $api.useQuery('get', '/api/v1/admin/jobs/{key}/runs/{id}', { params: { path: { key, id: runId } } });
  const { name } = jobName(t, key);
  const crumbs = [
    { label: t('nav.admin'), to: '/admin' as const },
    { label: t('admin.jobs.title'), to: '/admin/jobs' as const },
    { label: name, to: '/admin/jobs/$key' as const, params: { key } },
  ];

  if (run.error) return <div className="space-y-6"><PageHeader crumbs={crumbs} title={name} /><ProblemMessage problem={present(toProblem(run.error))} /></div>;
  if (!run.data) return <div className="space-y-6"><PageHeader crumbs={crumbs} title={name} /><AdminSkeleton /></div>;
  const r = run.data;

  return (
    <div className="space-y-6">
      <PageHeader crumbs={crumbs} title={t('admin.jobs.run_title', { date: formatInstant(r.startedAt) })} meta={<JobOutcome outcome={r.outcome} />}>
        <StatLine items={[
          { label: t('admin.jobs.started'), value: formatInstant(r.startedAt) },
          { label: t('admin.jobs.finished'), value: r.finishedAt ? formatInstant(r.finishedAt) : t('admin.jobs.outcomes.running') },
          { label: t('admin.jobs.duration'), value: r.durationMs != null ? formatDuration(r.durationMs) : t('admin.jobs.outcomes.running') },
          { label: t('admin.jobs.trigger'), value: t(`admin.jobs.triggers.${r.trigger}`, { defaultValue: r.trigger }) },
        ]} />
      </PageHeader>
      {r.summary ? (
        <Section title={t('admin.jobs.summary')}>
          <p className={r.outcome === 'failed' ? 'font-mono text-sm break-all text-status-critical' : 'font-mono text-sm break-all'}>{r.summary}</p>
        </Section>
      ) : null}
      <Section title={t('admin.jobs.logs')}>
        {r.logs ? (
          <pre className="max-h-[32rem] overflow-auto rounded-md bg-muted p-3 font-mono text-xs whitespace-pre">{r.logs.join('\n')}</pre>
        ) : (
          <p className="text-muted-foreground">{r.logsAvailable ? t('admin.jobs.no_logs') : t('admin.jobs.logs_off')}</p>
        )}
      </Section>
    </div>
  );
}
