import { useTranslation } from 'react-i18next';
import { EmptyState } from '@/components/EmptyState';

/** Placeholder until 1E2 builds the list. */
export function SubscriptionsPage() {
  const { t } = useTranslation();
  return (
    <>
      <h1 className="text-2xl font-semibold">{t('nav.subscriptions')}</h1>
      <EmptyState title={t('subscriptions.empty_title')} body={t('subscriptions.empty_body')} />
    </>
  );
}
