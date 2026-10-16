import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Ellipsis } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ScopeSwitch } from '@/components/ScopeSwitch';
import { Button } from '@/components/ui/button';
import { Guides } from '@/features/guides/Guides';
import { section, visibleNav, type NavItem } from './nav';
import { useSession } from './Providers';

const LINK = 'flex min-h-11 items-center gap-2 rounded-md px-3 py-2 hover:bg-accent hover:text-accent-foreground';
// A phone shares its width among five tabs: icon above a short label, each tab an equal share (2E1 final review).
const TAB = 'flex min-h-11 min-w-0 flex-1 flex-col items-center justify-center gap-0.5 px-1 py-1 text-xs hover:bg-accent hover:text-accent-foreground';

/** Sidebar on desktop; on mobile, four tabs and « Plus » (screens.md §1). */
export function Shell() {
  const { t } = useTranslation();
  const { capabilities, signOut } = useSession();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  const items = visibleNav(capabilities);
  const more = items.filter((item) => item.mobile === 'more');
  const inMore = pathname === '/more'
    || more.some((item) => pathname === section(item.to) || pathname.startsWith(`${section(item.to)}/`));

  const link = (item: NavItem, style = LINK) => (
    <Link key={item.to} to={item.to} className={style} activeProps={{ 'aria-current': 'page', className: 'font-semibold' }}>
      <item.icon aria-hidden="true" className="size-5 shrink-0" />
      <span className="max-w-full truncate">{t(item.label)}</span>
    </Link>
  );

  return (
    <div className="min-h-dvh md:grid md:grid-cols-[14rem_1fr]">
      <nav aria-label={t('nav.sidebar')} className="hidden gap-1 border-r bg-sidebar p-4 text-sidebar-foreground md:flex md:flex-col">
        <p className="mb-4 px-3 text-lg font-semibold">{t('app.name')}</p>
        {items.map((item) => link(item))}
      </nav>
      <div className="flex min-h-dvh flex-col pb-16 md:pb-0">
        <header className="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-3 md:gap-4">
          <p className="font-semibold md:invisible">{t('app.name')}</p>
          <ScopeSwitch />
          <div className="flex items-center gap-2">
            <Guides />
            <Button variant="ghost" onClick={() => void signOut()}>{t('shell.sign_out')}</Button>
          </div>
        </header>
        <main className="flex-1 p-4">
          <Outlet />
        </main>
      </div>
      <nav aria-label={t('nav.tabs')} className="fixed inset-x-0 bottom-0 flex border-t bg-background md:hidden">
        {items.filter((item) => item.mobile === 'tab').map((item) => link(item, TAB))}
        <Link to="/more" aria-current={inMore ? 'page' : undefined} className={inMore ? `${TAB} font-semibold` : TAB}>
          <Ellipsis aria-hidden="true" className="size-5 shrink-0" />
          <span className="max-w-full truncate">{t('nav.more')}</span>
        </Link>
      </nav>
    </div>
  );
}
