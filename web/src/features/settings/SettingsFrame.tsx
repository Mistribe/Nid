import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { tabsListVariants } from '@/components/ui/tabs';
import { cn } from '@/lib/utils';
import '@/locales/notifications';

const SECTIONS = [
  { to: '/settings/appearance', label: 'settings_frame.nav.appearance' },
  { to: '/settings/notifications', label: 'settings_frame.nav.notifications' },
] as const;

/** Every settings screen: the title, the links between the settings pages, the content. */
export function SettingsFrame({ title, children }: { title: string; children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <PageHeader title={title} />
      <nav aria-label={t('settings_frame.nav.label')} className={cn(tabsListVariants({ variant: 'line' }), 'h-auto max-w-full justify-start overflow-x-auto overflow-y-hidden border-b pb-[5px]')}>
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
