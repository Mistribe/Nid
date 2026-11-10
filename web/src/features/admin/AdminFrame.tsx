import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import '@/locales/admin';

/** Every admin screen: the Administration breadcrumb, the title and its actions, the content. */
export function AdminFrame({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.admin'), to: '/admin' }]} title={title} actions={actions} />
      {children}
    </div>
  );
}
