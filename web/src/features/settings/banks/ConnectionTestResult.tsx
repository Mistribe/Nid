import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import '@/locales/admin';

/** What a connection test to Enable Banking found, for the administrator's application and an adult's own. `redirectUrl` is only known to the administrator. */
export function ConnectionTestResult({ result, redirectUrl }: { result: components['schemas']['BankingConnectionTestView']; redirectUrl?: string }) {
  const { t } = useTranslation();
  if (result.outcome === 'rejected')
    return <p className="text-sm text-status-warning-text">{t('admin.banking.test_rejected')} <code>{result.code}</code></p>;
  if (result.outcome !== 'accepted') return <p className="text-sm text-status-warning-text">{t('admin.banking.test_unreachable')}</p>;
  return (
    <>
      <p className="text-sm text-status-good-text">{t('admin.banking.test_accepted')}</p>
      {result.active === false ? <p className="text-sm text-status-warning-text">{t('admin.banking.test_inactive')}</p> : null}
      {result.redirectUrlAllowed === false
        ? <p className="text-sm text-status-warning-text">{redirectUrl ? t('admin.banking.test_redirect_missing', { redirectUrl }) : t('admin.banking.test_redirect_missing_own')}</p> : null}
    </>
  );
}
