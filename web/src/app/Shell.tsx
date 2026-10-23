import { lazy, Suspense, useEffect, useState } from 'react';
import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Bird, Ellipsis, Search } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { ScopeSwitch } from '@/components/ScopeSwitch';
import { Guides } from '@/features/guides/Guides';
import { PendingBadge } from '@/features/proposals/PendingBadge';
import { TokenBanner } from '@/features/security/TokenBanner';
import { RoutineBadge } from './RoutineBadge';
import { section, TAB_ORDER, visibleNav, type NavItem } from './nav';
import { useSession } from './Providers';

// The menu's Radix code stays out of the initial bundle (200 kB budget); the name shows meanwhile.
const PersonMenu = lazy(() => import('./PersonMenu').then((m) => ({ default: m.PersonMenu })));
const VersionInfo = lazy(() => import('./VersionInfo').then((m) => ({ default: m.VersionInfo })));
const VERSION = import.meta.env.VITE_NID_VERSION ?? 'dev';
// cmdk and the dialog load on the first ⌘K (200 kB budget).
const CommandMenu = lazy(() => import('./CommandMenu').then((m) => ({ default: m.CommandMenu })));
const MAC = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform);

const LINK = 'flex min-h-11 items-center gap-2 rounded-md px-3 py-2 transition-colors hover:bg-accent hover:text-accent-foreground';
// The current page: a tinted pill in the accent colour, the text bold so the colour is never alone.
const ACTIVE = 'bg-primary/10 font-semibold text-primary hover:bg-primary/15 hover:text-primary';
// A phone shares its width among five tabs: icon above a short label, each tab an equal share (2E1 final review).
const TAB = 'flex min-h-11 min-w-0 flex-1 flex-col items-center justify-center gap-0.5 px-1 py-1 text-xs hover:bg-accent hover:text-accent-foreground';

/** Sidebar on desktop; on mobile, four tabs and « Plus » (screens.md §1). */
export function Shell() {
  const { t } = useTranslation();
  const { capabilities, session } = useSession();
  const pathname = useRouterState({ select: (s) => s.location.pathname });
  // null until first opened: the palette's code is not fetched before it is wanted.
  const [palette, setPalette] = useState<boolean | null>(null);
  useEffect(() => {
    const toggle = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        setPalette((open) => !open);
      }
    };
    window.addEventListener('keydown', toggle);
    return () => window.removeEventListener('keydown', toggle);
  }, []);
  const items = visibleNav(capabilities);
  const side = items.filter((item) => item.group !== 'account');
  const groups = (['follow', 'estate', 'budget'] as const).map((group) => ({ group, entries: side.filter((item) => item.group === group) })).filter((g) => g.entries.length > 0);
  const more = items.filter((item) => item.mobile === 'more');
  const inMore = pathname === '/more'
    || more.some((item) => pathname === section(item.to) || pathname.startsWith(`${section(item.to)}/`));

  const link = (item: NavItem, style = LINK) => (
    <Link key={item.to} to={item.to} className={style} activeProps={{ 'aria-current': 'page', className: style === LINK ? ACTIVE : 'font-semibold text-primary' }}>
      <item.icon aria-hidden="true" className="size-5 shrink-0" />
      <span className="max-w-full truncate">{t(item.label)}</span>
      {item.to === '/proposals' ? <PendingBadge /> : null}
      {item.to === '/routine' ? <RoutineBadge /> : null}
    </Link>
  );

  return (
    <div className="min-h-dvh md:grid md:grid-cols-[14rem_minmax(0,1fr)] print:block">
      <nav aria-label={t('nav.sidebar')} className="hidden gap-1 border-r print:hidden bg-sidebar p-4 text-sidebar-foreground md:sticky md:top-0 md:flex md:h-dvh md:flex-col md:overflow-y-auto">
        <p className="mb-2 flex items-center gap-2 px-3 text-lg font-semibold">
          <span aria-hidden="true" className="flex size-7 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-card"><Bird className="size-4" /></span>
          {t('app.name')}
        </p>
        {groups.map(({ group, entries }) => (
          <div key={group} className="mt-3">
            <h2 className="px-3 pb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">{t(`nav.group_${group}`)}</h2>
            <ul className="space-y-1">{entries.map((item) => <li key={item.to}>{link(item)}</li>)}</ul>
          </div>
        ))}
        <div className="mt-auto">
          <Suspense fallback={<p className="truncate px-3 py-2 text-sm font-medium">{session.person.name}</p>}>
            <PersonMenu />
          </Suspense>
          <Suspense fallback={<p className="px-3 pt-1 text-xs text-muted-foreground">{t('app.version', { version: VERSION })}</p>}>
            <VersionInfo version={VERSION} />
          </Suspense>
        </div>
      </nav>
      <div className="flex min-h-dvh flex-col pb-16 md:pb-0">
        {/* Sticky over the page it scrolls; the search opens the ⌘K palette, the scope and the guides sit at the end. */}
        <header className="sticky top-0 z-30 border-b bg-background/85 backdrop-blur print:hidden">
          <div className="mx-auto flex w-full max-w-7xl items-center gap-2 px-4 py-2.5 md:gap-4">
            <p className="font-semibold md:hidden">{t('app.name')}</p>
            <button type="button" onClick={() => setPalette(true)} aria-label={t('command.open')} aria-keyshortcuts={MAC ? 'Meta+K' : 'Control+K'}
              className="flex h-9 items-center gap-2 rounded-md border bg-card px-2.5 text-sm text-muted-foreground shadow-xs transition-colors outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 md:w-full md:max-w-sm">
              <Search aria-hidden="true" className="size-4 shrink-0" />
              <span aria-hidden="true" className="hidden flex-1 text-left md:inline">{t('command.open')}</span>
              <kbd aria-hidden="true" className="hidden rounded border bg-muted px-1.5 font-sans text-xs md:inline">{t(MAC ? 'command.shortcut_mac' : 'command.shortcut')}</kbd>
            </button>
            <div className="ml-auto flex items-center gap-2"><ScopeSwitch /><Guides /></div>
          </div>
        </header>
        {palette !== null ? <Suspense fallback={null}><CommandMenu open={palette} onOpenChange={setPalette} /></Suspense> : null}
        <TokenBanner />
        <main className="flex-1 p-4">
          {/* A line of reading and charts that stay legible on a wide screen: past 1 280 px the page centres. */}
          <div className="mx-auto w-full max-w-7xl"><Outlet /></div>
        </main>
      </div>
      <nav aria-label={t('nav.tabs')} className="fixed inset-x-0 bottom-0 flex border-t bg-background md:hidden print:hidden">
        {items.filter((item) => item.mobile === 'tab').sort((a, b) => TAB_ORDER.indexOf(a.to) - TAB_ORDER.indexOf(b.to)).map((item) => link(item, TAB))}
        <Link to="/more" aria-current={inMore ? 'page' : undefined} className={inMore ? `${TAB} font-semibold text-primary` : TAB}>
          <Ellipsis aria-hidden="true" className="size-5 shrink-0" />
          <span className="max-w-full truncate">{t('nav.more')}</span>
          {more.some((item) => item.to === '/proposals') ? <PendingBadge /> : null}
        </Link>
      </nav>
    </div>
  );
}
