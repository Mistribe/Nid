import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { PageHeader } from '@/components/page/PageHeader';
import { tabsListVariants } from '@/components/ui/tabs';
import { cn } from '@/lib/utils';
import { useBankingStatus } from '@/api/banking';
import '@/locales/notifications';

const SECTIONS = [
  { to: '/settings/appearance', label: 'settings_frame.nav.appearance' },
  { to: '/settings/notifications', label: 'settings_frame.nav.notifications' },
  { to: '/settings/projection', label: 'settings_frame.nav.projection' },
  { to: '/settings/persons', label: 'settings_frame.nav.persons' },
  { to: '/settings/calendar', label: 'settings_frame.nav.calendar' },
] as const;
/** Shown only while the household has bank sync on. */
const BANKS = { to: '/settings/banks', label: 'settings_frame.nav.banks' } as const;

/** Every settings screen: « Réglages » and the page's actions, the links between the settings pages, the content. `title` is the page's tab, read in the trail. */
export function SettingsFrame({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  const { t } = useTranslation();
  const banking = useBankingStatus().data?.enabled === true;
  return (
    <div className="max-w-2xl space-y-6">
      <PageHeader title={t('settings_frame.nav.label')} crumbLabel={title} actions={actions} />
      <nav aria-label={t('settings_frame.nav.label')} className={cn(tabsListVariants({ variant: 'line' }), 'h-auto max-w-full justify-start overflow-x-auto overflow-y-hidden border-b pb-[5px]')}>
        {(banking ? [...SECTIONS, BANKS] : SECTIONS).map((section) => (
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
