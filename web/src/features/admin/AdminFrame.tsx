import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { Skeleton } from '@/components/ui/skeleton';
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

/**
 * The shape of what is loading, never a spinner (screens.md §10). No `role="status"`: a screen uses that role
 * for a test send's outcome, and tests find it by it.
 */
export function AdminSkeleton() {
  const { t } = useTranslation();
  return (
    <Skeleton aria-busy="true" className="h-32">
      <span className="sr-only">{t('admin.loading')}</span>
    </Skeleton>
  );
}
