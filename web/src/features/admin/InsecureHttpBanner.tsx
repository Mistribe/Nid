import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import '@/locales/admin';

/**
 * Issue 215 (local-accounts spec §12): the hoster allowed plain HTTP (`Auth__AllowInsecureHttp`), so passwords and the
 * session cookie may travel unencrypted. Said on every admin screen, as the host's log says it at every start.
 */
export function InsecureHttpBanner() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const insecure = $api.useQuery('get', '/api/v1/admin/system').data?.auth.insecureHttp ?? false;
  if (!insecure) return null;
  return (
    <p role="status" className="rounded-md border border-status-warning/60 bg-status-warning/10 p-3 text-sm">
      {t('admin.system.insecure_http')}
    </p>
  );
}
