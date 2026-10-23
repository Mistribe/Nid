import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { Skeleton } from '@/components/ui/skeleton';
import { tabsListVariants } from '@/components/ui/tabs';
import { cn } from '@/lib/utils';
import '@/locales/admin';

/** The sections of screens.md §12. */
const SECTIONS = [
  { to: '/admin/users', label: 'admin.nav.users' },
  { to: '/admin/household', label: 'admin.nav.household' },
  { to: '/admin/fx', label: 'admin.nav.fx' },
  { to: '/admin/prices', label: 'admin.nav.prices' },
  { to: '/admin/catalog', label: 'admin.nav.catalog' },
  { to: '/admin/jobs', label: 'admin.nav.jobs' },
  { to: '/admin/system', label: 'admin.nav.system' },
  { to: '/admin/security', label: 'admin.nav.security' },
] as const;

/** Every admin screen: the section links, the title, the content. */
export function AdminFrame({ title, children }: { title: string; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-6">
      <PageHeader title={title} />
      <nav aria-label={t('admin.nav.label')} className={cn(tabsListVariants({ variant: 'line' }), 'h-auto max-w-full justify-start overflow-x-auto overflow-y-hidden border-b pb-[5px]')}>
        {SECTIONS.map((section) => (
          <Link
            key={section.to}
            to={section.to}
            className="relative inline-flex min-h-9 items-center rounded-md px-3 py-1 text-sm font-medium whitespace-nowrap text-foreground/60 hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
            activeProps={{ 'aria-current': 'page', className: 'text-foreground after:absolute after:inset-x-0 after:-bottom-[5px] after:h-0.5 after:bg-foreground' }}
          >
            {t(section.label)}
          </Link>
        ))}
      </nav>
      {children}
    </div>
  );
}

/**
 * The shape of what is loading, never a spinner (screens.md §10). No `role="status"`: the screens use that role
 * for their confirmations, and tests find those by it.
 */
export function AdminSkeleton() {
  const { t } = useTranslation();
  return (
    <Skeleton aria-busy="true" className="h-32">
      <span className="sr-only">{t('admin.loading')}</span>
    </Skeleton>
  );
}
