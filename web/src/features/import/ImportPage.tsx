import { useTranslation } from 'react-i18next';
import { EmptyState } from '@/components/EmptyState';

/** Placeholder until 1E3 builds the import wizard. */
export function ImportPage() {
  const { t } = useTranslation();
  return (
    <>
      <h1 className="text-2xl font-semibold">{t('nav.import')}</h1>
      <EmptyState title={t('placeholder.title')} body={t('placeholder.import')} />
    </>
  );
}
