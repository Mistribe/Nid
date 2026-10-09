import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Ellipsis } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ScopeSwitch } from '@/components/ScopeSwitch';
import { Button } from '@/components/ui/button';
import { section, visibleNav, type NavItem } from './nav';
import { useSession } from './Providers';

const LINK = 'flex min-h-11 items-center gap-2 rounded-md px-3 py-2 hover:bg-accent hover:text-accent-foreground';

/** Sidebar on desktop; on mobile, four tabs and « Plus » (screens.md §1). */
export function Shell() {
  const { t } = useTranslation();
  const { capabilities, signOut } = useSession();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const items = visibleNav(capabilities);
  const more = items.filter((item) => item.mobile === 'more');
  const inMore = pathname === '/more'
    || more.some((item) => pathname === section(item.to) || pathname.startsWith(`${section(item.to)}/`));

  const link = (item: NavItem) => (
    <Link key={item.to} to={item.to} className={LINK} activeProps={{ 'aria-current': 'page', className: 'font-semibold' }}>
      <item.icon aria-hidden="true" className="size-5" />
      <span>{t(item.label)}</span>
    </Link>
  );

  return (
    <div className="min-h-dvh md:grid md:grid-cols-[14rem_1fr]">
      <nav aria-label={t('nav.sidebar')} className="hidden gap-1 border-r bg-sidebar p-4 text-sidebar-foreground md:flex md:flex-col">
        <p className="mb-4 px-3 text-lg font-semibold">{t('app.name')}</p>
        {items.map(link)}
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
        {items.filter((item) => item.mobile === 'tab').map(link)}
        <Link to="/more" aria-current={inMore ? 'page' : undefined} className={inMore ? `${LINK} font-semibold` : LINK}>
          <Ellipsis aria-hidden="true" className="size-5" />
          <span>{t('nav.more')}</span>
        </Link>
      </nav>
    </div>
  );
}
