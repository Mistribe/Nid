import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant } from './format';

/** screens.md §12, Tâches: read only. */
export function JobsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const jobs = $api.useQuery('get', '/api/v1/admin/jobs');

  const title = t('admin.jobs.title');
  if (jobs.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(jobs.error))} /></AdminFrame>;
  if (!jobs.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;
  if (jobs.data.length === 0)
    return <AdminFrame title={title}><EmptyState title={t('admin.jobs.empty_title')} body={t('admin.jobs.empty_body')} /></AdminFrame>;

  return (
    <AdminFrame title={title}>
      <table className="w-full text-left">
        <thead>
          <tr className="border-b">
            <th scope="col" className="py-2 pr-4">{t('admin.jobs.key')}</th>
            <th scope="col" className="py-2 pr-4">{t('admin.jobs.last')}</th>
            <th scope="col" className="py-2 pr-4">{t('admin.jobs.next')}</th>
            <th scope="col" className="py-2">{t('admin.jobs.state')}</th>
          </tr>
        </thead>
        <tbody>
          {jobs.data.map((job) => (
            <tr key={job.key} className="border-b">
              <td className="py-2 pr-4 font-mono">{job.key}</td>
              <td className="py-2 pr-4">{job.lastRunAt ? formatInstant(job.lastRunAt) : t('admin.jobs.never')}</td>
              <td className="py-2 pr-4">{job.nextRunAt ? formatInstant(job.nextRunAt) : t('admin.jobs.on_demand')}</td>
              <td className="py-2">{t(job.state === 'running' ? 'admin.jobs.running' : 'admin.jobs.idle')}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </AdminFrame>
  );
}
