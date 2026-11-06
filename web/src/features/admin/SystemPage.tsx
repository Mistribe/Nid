import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { AdminFrame, AdminSkeleton } from './AdminFrame';
import { formatInstant, formatMegabytes } from './format';
import '@/locales/admin';

/** screens.md §12, Système: read only; no item count (authorization.md §8). */
export function SystemPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const system = $api.useQuery('get', '/api/v1/admin/system');

  const title = t('admin.system.title');
  if (system.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(system.error))} /></AdminFrame>;
  if (!system.data) return <AdminFrame title={title}><AdminSkeleton /></AdminFrame>;
  const status = system.data;
  const health = (value: string) => t(value === 'healthy' ? 'admin.system.healthy' : 'admin.system.unhealthy');

  return (
    <AdminFrame title={title}>
      <dl className="grid max-w-lg grid-cols-[auto_1fr] gap-x-6 gap-y-2">
        <dt>{t('admin.system.version')}</dt>
        <dd className="font-mono">{status.version}</dd>
        <dt>{t('admin.system.started')}</dt>
        <dd>{formatInstant(status.startedAt)}</dd>
        <dt>{t('admin.system.database')}</dt>
        <dd>{health(status.health.database)}</dd>
        <dt>{t('admin.system.identity_provider')}</dt>
        <dd>{health(status.health.identityProvider)}</dd>
        <dt>{t('admin.system.size')}</dt>
        <dd>{status.databaseSizeBytes == null ? t('admin.system.unknown') : t('admin.system.megabytes', { size: formatMegabytes(status.databaseSizeBytes) })}</dd>
      </dl>
      <p><Link to="/changelog" className="underline">{t('app.changelog.see_all')}</Link></p>
    </AdminFrame>
  );
}
