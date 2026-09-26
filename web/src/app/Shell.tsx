import { Link, Outlet } from '@tanstack/react-router';
import { Download, KeyRound, Repeat, ShieldCheck, Settings, Upload } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ScopeSwitch } from '@/components/ScopeSwitch';
import { Button } from '@/components/ui/button';
import { useSession } from './Providers';

const NAV = [
  { module: 'subscriptions', admin: false, to: '/subscriptions', icon: Repeat, label: 'nav.subscriptions' },
  { module: 'import', admin: false, to: '/import', icon: Upload, label: 'nav.import' },
  { module: 'core', admin: false, to: '/export', icon: Download, label: 'nav.export' },
  { module: null, admin: false, to: '/security', icon: KeyRound, label: 'nav.security' },
  { module: null, admin: false, to: '/settings/appearance', icon: Settings, label: 'nav.settings' },
  { module: null, admin: true, to: '/admin/users', icon: ShieldCheck, label: 'nav.admin' },
] as const;

/** Sidebar on desktop, bottom tabs on mobile, same entries (screens.md §1). */
export function Shell() {
  const { t } = useTranslation();
  const { capabilities, signOut } = useSession();
  const items = NAV.filter((item) =>
    (item.module === null || capabilities.modules.includes(item.module)) && (!item.admin || capabilities.admin));

  const links = items.map((item) => (
    <Link
      key={item.to}
      to={item.to}
      search={(prev) => ({ scope: prev.scope })}
      className="flex min-h-11 items-center gap-2 rounded-md px-3 py-2 hover:bg-accent hover:text-accent-foreground"
      activeProps={{ 'aria-current': 'page', className: 'font-semibold' }}
    >
      <item.icon aria-hidden="true" className="size-5" />
      <span>{t(item.label)}</span>
    </Link>
  ));

  return (
    <div className="min-h-dvh md:grid md:grid-cols-[14rem_1fr]">
      <nav aria-label={t('nav.sidebar')} className="hidden gap-1 border-r bg-sidebar p-4 text-sidebar-foreground md:flex md:flex-col">
        <p className="mb-4 px-3 text-lg font-semibold">{t('app.name')}</p>
        {links}
      </nav>
      <div className="flex min-h-dvh flex-col pb-16 md:pb-0">
        <header className="flex items-center justify-between gap-4 border-b px-4 py-3">
          <p className="font-semibold md:invisible">{t('app.name')}</p>
          <ScopeSwitch />
          <Button variant="ghost" onClick={() => void signOut()}>{t('shell.sign_out')}</Button>
        </header>
        <main className="flex-1 p-4">
          <Outlet />
        </main>
      </div>
      <nav aria-label={t('nav.tabs')} className="fixed inset-x-0 bottom-0 flex justify-around border-t bg-background md:hidden">
        {links}
      </nav>
    </div>
  );
}
