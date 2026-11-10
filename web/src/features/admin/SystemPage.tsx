import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { StatLine } from '@/components/page/StatLine';
import { AdminFrame } from './AdminFrame';
import { Loading, StatLineSkeleton } from '@/components/page/skeletons';
import { formatInstant } from '@/lib/format';
import { formatMegabytes } from './format';
import '@/locales/admin';

/** screens.md §12, Système: read only; no item count (authorization.md §8). */
export function SystemPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const system = $api.useQuery('get', '/api/v1/admin/system');

  const title = t('admin.system.title');
  if (system.error) return <AdminFrame title={title}><ProblemMessage problem={present(toProblem(system.error))} /></AdminFrame>;
  if (!system.data) return <AdminFrame title={title}><Loading label={t('admin.loading')}><StatLineSkeleton items={5} /></Loading></AdminFrame>;
  const status = system.data;
  const health = (value: string) => t(value === 'healthy' ? 'admin.system.healthy' : 'admin.system.unhealthy');

  return (
    <AdminFrame title={title}>
      <StatLine items={[
        { label: t('admin.system.version'), value: <span className="font-mono">{status.version}</span> },
        { label: t('admin.system.started'), value: formatInstant(status.startedAt) },
        { label: t('admin.system.database'), value: health(status.health.database) },
        { label: t('admin.system.identity_provider'), value: health(status.health.identityProvider) },
        { label: t('admin.system.size'), value: status.databaseSizeBytes == null ? t('admin.system.unknown') : t('admin.system.megabytes', { size: formatMegabytes(status.databaseSizeBytes) }) },
      ]} />
      <p><Link to="/changelog" className="underline">{t('app.changelog.see_all')}</Link></p>
    </AdminFrame>
  );
}
