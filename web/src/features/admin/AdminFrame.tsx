import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/** The sections of screens.md §12. Tasks 10 and 11 of the 1G plan add theirs here. */
const SECTIONS = [
  { to: '/admin/users', label: 'admin.nav.users' },
] as const;

/** Every admin screen: the section links, the title, the content. */
export function AdminFrame({ title, children }: { title: string; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="space-y-6">
      <nav aria-label={t('admin.nav.label')} className="flex flex-wrap gap-2 border-b pb-2">
        {SECTIONS.map((section) => (
          <Link
            key={section.to}
            to={section.to}
            search={(prev) => ({ scope: prev.scope })}
            className="min-h-11 rounded-md px-3 py-2 hover:bg-accent hover:text-accent-foreground"
            activeProps={{ 'aria-current': 'page', className: 'font-semibold' }}
          >
            {t(section.label)}
          </Link>
        ))}
      </nav>
      <h1 className="text-2xl font-semibold">{title}</h1>
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
    <div aria-busy="true" className="h-32 animate-pulse rounded-md bg-muted">
      <span className="sr-only">{t('admin.loading')}</span>
    </div>
  );
}
