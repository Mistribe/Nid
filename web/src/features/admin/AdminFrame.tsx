import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { InsecureHttpBanner } from './InsecureHttpBanner';
import '@/locales/admin';

/** Every admin screen: the Administration breadcrumb, the title and its actions, the figures under them (`header`), the content. */
export function AdminFrame({ title, actions, header, children }: { title: string; actions?: ReactNode; header?: ReactNode; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-6">
      <PageHeader crumbs={[{ label: t('nav.admin'), to: '/admin' }]} title={title} actions={actions}>{header}</PageHeader>
      <InsecureHttpBanner />
      {children}
    </div>
  );
}
