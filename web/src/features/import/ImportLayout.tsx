import { Outlet } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { DraftProvider } from './draft';

/** The import area: one title, and the prepared file shared by its two routes. */
export function ImportLayout() {
  const { t } = useTranslation();
  return (
    <DraftProvider>
      <div className="space-y-6">
        <PageHeader title={t('nav.import')} />
        <Outlet />
      </div>
    </DraftProvider>
  );
}
