import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
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
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t('admin.jobs.key')}</TableHead>
            <TableHead>{t('admin.jobs.last')}</TableHead>
            <TableHead>{t('admin.jobs.next')}</TableHead>
            <TableHead>{t('admin.jobs.state')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {jobs.data.map((job) => (
            <TableRow key={job.key}>
              <TableCell className="font-mono">{job.key}</TableCell>
              <TableCell>{job.lastRunAt ? formatInstant(job.lastRunAt) : t('admin.jobs.never')}</TableCell>
              <TableCell>{job.nextRunAt ? formatInstant(job.nextRunAt) : t('admin.jobs.on_demand')}</TableCell>
              <TableCell>{t(job.state === 'running' ? 'admin.jobs.running' : 'admin.jobs.idle')}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </AdminFrame>
  );
}
