import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { buttonVariants } from '@/components/ui/button';
import { useBankingStatus } from '@/api/banking';
import '@/locales/banks';

/** « Opérations à relier », in a page's header actions, once bank sync is on. */
export function ToLinkLink() {
  const { t } = useTranslation();
  if (useBankingStatus().data?.enabled !== true) return null;
  return <Link to="/operations" className={buttonVariants({ variant: 'outline', size: 'sm' })}>{t('banks.operations.to_link')}</Link>;
}
