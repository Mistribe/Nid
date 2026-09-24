import { Outlet } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { DraftProvider } from './draft';

/** The import area: one title, and the prepared file shared by its two routes. */
export function ImportLayout() {
  const { t } = useTranslation();
  return (
    <DraftProvider>
      <div className="space-y-6">
        <h1 className="text-2xl font-semibold">{t('nav.import')}</h1>
        <Outlet />
      </div>
    </DraftProvider>
  );
}
